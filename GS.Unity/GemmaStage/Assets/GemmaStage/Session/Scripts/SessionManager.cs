using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Cysharp.Threading.Tasks;
using GemmaStage.Audience;
using GemmaStage.Boards;
using GemmaStage.Core;
using GemmaStage.Evaluation;
using GemmaStage.FinalQA;
using GemmaStage.Results;
using GemmaStage.LiveQA;
using GemmaStage.Lobby;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace GemmaStage.Session
{
    // Owns the active session lifecycle as a singleton in the Shared scene.
    //
    // 5.4.1 shipped the skeleton (singleton, public API, lobby subscription,
    // config snapshot). 5.4.2 fills the StartSession orchestration body
    // (env load → slides → ray mode → audio → echo → SessionLayer.Begin →
    // wrist timer last). EndSession / BeginEvaluation / CancelEvaluation are
    // still stubs; they're filled by 5.9 / 5.11.
    //
    // Lives in Assembly-CSharp (no asmdef) because it needs to type-reference
    // SessionSetupPanel which itself lives in Assembly-CSharp.
    public class SessionManager : MonoBehaviour
    {
        public static SessionManager Instance { get; private set; }

        const string LobbySceneName = "Lobby";

        [SerializeField] RayController                             _rayController;
        [SerializeField] AudioPipeline                            _audioPipeline;
        [SerializeField] EchoProcessor                            _echoProcessor;
        [SerializeField] SessionLayer                             _sessionLayer;
        [SerializeField] LiveQaController                         _liveQaController;
        [SerializeField] FinalQaController                        _finalQaController;
        [SerializeField] EvaluationProgressController             _evaluationProgressController;
        [SerializeField] ResultsController                        _resultsController;

        [Tooltip("World-space StartSpeechPanel prefab. Shown after the env + LLM finish loading, before audio capture / the wrist timer kick in. Pressing the button completes the live-phase startup.")]
        [SerializeField] StartSpeechPanelView                     _startSpeechPanelPrefab;

        LobbySessionConfig _sessionConfig;
        GameSettings _gameSettings;
        bool _sessionActive;
        SessionSetupPanel _subscribedPanel;
        RuntimeSlideSource _runtimeSlideSource;
        StartSpeechPanelView _startSpeechPanel;
        TaskCompletionSource<bool> _startSpeechPressedTcs;

        public bool SessionActive => _sessionActive;
        public LobbySessionConfig CurrentSessionConfig => _sessionConfig;
        public GameSettings CurrentGameSettings => _gameSettings;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            // No DontDestroyOnLoad — this manager already lives in Shared,
            // which stays loaded across the Lobby/environment swaps.

            ResolveCollaborators();
        }

        void OnEnable()
        {
            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= HandleSceneLoaded;
            UnsubscribeFromPanel();
        }

        void Start()
        {
            // Defensive lookup — Lobby may already be loaded by the time our
            // OnEnable fires (additive scene ordering between Shared and Lobby
            // is not guaranteed).
            TrySubscribeToLobbyPanel();
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void ResolveCollaborators()
        {
            // Defensive fallback — the Inspector wiring is the source of truth,
            // but a missing reference shouldn't take the orchestration down.
            if (_rayController == null) _rayController = FindAnyObjectByType<RayController>();
            if (_audioPipeline == null) _audioPipeline = FindAnyObjectByType<AudioPipeline>();
            if (_echoProcessor == null) _echoProcessor = FindAnyObjectByType<EchoProcessor>();
            if (_sessionLayer == null) _sessionLayer = FindAnyObjectByType<SessionLayer>();
            if (_liveQaController == null) _liveQaController = FindAnyObjectByType<LiveQaController>();
            if (_finalQaController == null) _finalQaController = FindAnyObjectByType<FinalQaController>();
            if (_evaluationProgressController == null)
                _evaluationProgressController = FindAnyObjectByType<EvaluationProgressController>();
            if (_resultsController == null)
                _resultsController = FindAnyObjectByType<ResultsController>();

            if (_rayController == null) Debug.LogError("[SessionManager] RayController missing in Shared scene.");
            if (_audioPipeline == null) Debug.LogError("[SessionManager] AudioPipeline missing in Shared scene.");
            if (_echoProcessor == null) Debug.LogError("[SessionManager] EchoProcessor missing in Shared scene.");
            if (_sessionLayer == null) Debug.LogError("[SessionManager] SessionLayer missing in Shared scene.");
            if (_liveQaController == null) Debug.LogError("[SessionManager] LiveQaController missing in Shared scene.");
            if (_finalQaController == null) Debug.LogError("[SessionManager] FinalQaController missing in Shared scene.");
            if (_evaluationProgressController == null) Debug.LogError("[SessionManager] EvaluationProgressController missing in Shared scene.");
        }

        void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (scene.name == LobbySceneName)
                TrySubscribeToLobbyPanel();
        }

        void TrySubscribeToLobbyPanel()
        {
            var panel = FindAnyObjectByType<SessionSetupPanel>();
            if (panel == null) return;
            if (panel == _subscribedPanel) return;

            UnsubscribeFromPanel();
            panel.StartSessionRequested += HandleStartSessionRequested;
            _subscribedPanel = panel;
        }

        void UnsubscribeFromPanel()
        {
            if (_subscribedPanel == null) return;
            _subscribedPanel.StartSessionRequested -= HandleStartSessionRequested;
            _subscribedPanel = null;
        }

        void HandleStartSessionRequested(LobbySessionConfig config)
        {
            // The Game Settings panel unloads with the Lobby; pull
            // settings from the persisted store (canonical source — every
            // change writes through, see GameSettingsRepository / Feature 5.3).
            var settings = GameSettingsRepository.Load();
            StartSession(config, settings);
        }


        public async void StartSession(LobbySessionConfig sessionConfig, GameSettings gameSettings)
        {
            if (_sessionActive)
            {
                Debug.LogWarning("[SessionManager] StartSession called while a session is already active. Ignoring.");
                return;
            }

            // The Lobby scene is about to unload as part of the environment
            // transition, so drop our panel subscription before scene teardown.
            UnsubscribeFromPanel();

            // Step 0: snapshot. Holding the LobbySessionConfig reference also pins
            // the slide RenderedTexture lifetimes past Lobby unload — those
            // textures were allocated by PresentationPickerPopup and would
            // otherwise be orphaned when the popup's GameObject is destroyed.
            _sessionConfig = sessionConfig;
            _gameSettings = gameSettings;

            var gm = GameManager.Instance;
            if (gm == null)
            {
                Debug.LogError("[SessionManager] StartSession aborted — GameManager.Instance is null.");
                _sessionConfig = null;
                _gameSettings = null;
                return;
            }

            // Step 0.5: black out the world before we start swapping scenes so the
            // player never sees the env scene's NPCs popping in one by one. Hidden
            // again after the spawner reports done, the LLM finishes loading, or
            // the NPC-wait timeout — whichever comes last.
            LoadingOverlayController.Instance?.Show();

            try
            {
                await gm.Environment.LoadEnvironment(sessionConfig.Environment);          // step 1
            }
            catch (Exception ex)
            {
                Debug.LogError($"[SessionManager] Environment load failed: {ex.Message}. Lobby stays loaded.");
                LoadingOverlayController.Instance?.Hide();
                _sessionConfig = null;
                _gameSettings = null;
                return;
            }

            await WaitForNpcSpawnAsync(sessionConfig.Environment);                        // step 1.25

            HandSlidesToPresentationBoard();                                              // step 1.5

            // Enable rays before showing the Start Speech popup so the player
            // can press its button. Live-phase B/Y → End Session binding becomes
            // active here too, but EndSessionController gates ShowPanel on
            // SessionActive (still false), so the button is a no-op until
            // we flip the flag below.
            _rayController?.SetMode(RayMode.Performance);                                 // step 2

            // Step 3: load the LLM model under the overlay. SessionLayer.Begin
            // returns a Task<bool> that completes once StartSessionOnWorker
            // finishes constructing the native engine. Audio capture and the
            // wrist timer stay deferred so nothing is fed into the model yet
            // and the live-phase clock doesn't start ticking.
            bool engineReady = true;
            if (_sessionLayer != null)
                engineReady = await _sessionLayer.Begin(sessionConfig, gameSettings);

            // Hide the overlay regardless of outcome — on failure the
            // SessionFailed event surfaces the error UI; on success we hand off
            // to the Start Speech popup.
            LoadingOverlayController.Instance?.Hide();

            if (!engineReady)
            {
                Debug.LogError("[SessionManager] LLM startup failed — aborting session start.");
                _sessionConfig = null;
                _gameSettings = null;
                _rayController?.SetMode(RayMode.Lobby);
                return;
            }

            // Step 4: wait for the player to confirm via the Start Speech
            // popup. Audio / Live Q&A / timer are all gated behind this press
            // so the session only begins consuming inputs once the player is
            // ready.
            await ShowStartSpeechPopupAndWaitAsync();

            // Step 5: live phase actually begins.
            _audioPipeline?.StartCapture(gameSettings.MicrophoneDeviceName);
            _echoProcessor?.Apply(gameSettings.EchoEnabled, gameSettings.EchoLevel,
                                  gameSettings.MicrophoneDeviceName);
            _liveQaController?.OnSessionStarted(sessionConfig);
            _finalQaController?.OnSessionStarted(sessionConfig);

            var timer = FindAnyObjectByType<WristTimer>();
            timer?.StartCountdown(sessionConfig.DurationMinutes);                         // step 6 — last

            _sessionActive = true;
        }

        // Shows the Start Speech popup in front of the player and returns once
        // the player presses the button (or once the panel prefab is missing,
        // in which case we log and continue so the dev path never wedges).
        async UniTask ShowStartSpeechPopupAndWaitAsync()
        {
            if (_startSpeechPanelPrefab == null)
            {
                Debug.LogWarning("[SessionManager] StartSpeechPanel prefab not assigned — skipping ready prompt.");
                return;
            }

            EnsureStartSpeechPanel();
            PositionStartSpeechPanelInFrontOfPlayer();

            _startSpeechPressedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startSpeechPanel.Show();
            try
            {
                await _startSpeechPressedTcs.Task;
            }
            finally
            {
                _startSpeechPanel.Hide();
                _startSpeechPressedTcs = null;
            }
        }

        void EnsureStartSpeechPanel()
        {
            if (_startSpeechPanel != null) return;
            // Parent under SessionManager (Shared scene) so the popup survives
            // any future scene swap; the lazy instance is reused across runs.
            _startSpeechPanel = Instantiate(_startSpeechPanelPrefab, transform);
            _startSpeechPanel.gameObject.SetActive(false);
            _startSpeechPanel.OnStartPressed += HandleStartSpeechPressed;
        }

        void HandleStartSpeechPressed()
        {
            _startSpeechPressedTcs?.TrySetResult(true);
        }

        // Eye-level placement, ~1.5m forward, matching the End Session popup
        // distance. LookAtCamera on the prefab keeps the normal aligned.
        void PositionStartSpeechPanelInFrontOfPlayer()
        {
            if (_startSpeechPanel == null) return;
            var cam = Camera.main;
            if (cam == null) return;

            var camPos = cam.transform.position;
            var forward = cam.transform.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 1e-6f) forward = Vector3.forward;
            forward.Normalize();

            var pos = camPos + forward * 1.5f;
            pos.y = camPos.y;
            _startSpeechPanel.transform.position = pos;
        }

        // Hard timeout caps the wait if the spawner ever wedges; envs without an audience return immediately.
        async UniTask WaitForNpcSpawnAsync(EnvironmentId env)
        {
            var sceneName = env switch
            {
                EnvironmentId.Stage     => "Stage",
                EnvironmentId.Classroom => "Classroom",
                _                       => null,
            };
            if (sceneName == null) return;

            var scene = SceneManager.GetSceneByName(sceneName);
            if (!scene.IsValid() || !scene.isLoaded) return;

            AudienceNpcSpawner spawner = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                spawner = root.GetComponentInChildren<AudienceNpcSpawner>(includeInactive: true);
                if (spawner != null) break;
            }
            if (spawner == null) return;

            await spawner.WaitUntilSpawnCompletedAsync(CancellationToken.None);

            // Force resumption on the Unity main thread. When the CTS above
            // fires before the spawn finishes, UniTask's AttachExternalCancellation
            // resumes the continuation on the timer-pool thread that triggered
            // the cancel — so the caller's next steps (LoadingOverlayController.Hide
            // → CanvasGroup.alpha, etc.) would touch UnityEngine APIs off-main-thread
            // and throw "can only be called from the main thread".
            await UniTask.SwitchToMainThread();
        }

        void HandSlidesToPresentationBoard()
        {
            if (_sessionConfig.Slides == null || _sessionConfig.Slides.Count == 0)
                return;

            var board = FindAnyObjectByType<PresentationBoard>(FindObjectsInactive.Include);
            if (board == null)
            {
                Debug.LogWarning("[SessionManager] No PresentationBoard found in the loaded environment scene; slides not pushed.");
                return;
            }

            var textures = _sessionConfig.Slides
                .Select(s => s.RenderedTexture)
                .Where(t => t != null)
                .ToArray();

            _runtimeSlideSource = ScriptableObject.CreateInstance<RuntimeSlideSource>();
            _runtimeSlideSource.SetSlides(textures);
            board.SetSource(_runtimeSlideSource);
        }

        public void EndSession()
        {
            if (!_sessionActive) return;
            _sessionActive = false;

            // Live phase is over — return the watch to wall-clock display in
            // the default colour so the player has a useful readout during
            // Final Q&A / Evaluation (penalty red would be misleading once the
            // countdown no longer drives the experience).
            var timer = FindAnyObjectByType<WristTimer>();
            timer?.SwitchToWallClock();

            // Module begins teardown. Phase 6 wires the real Session.End body;
            // today this just flips IsActive=false and logs.
            _sessionLayer?.End();

            // Drop any open Live Q&A popup so a question never bleeds into
            // Final Q&A / evaluation. Idempotent.
            _liveQaController?.OnSessionEnded();

            // Final Q&A is gated by LobbySessionConfig.FinalQaEnabled
            // (docs/SESSION_ARCHITECTURE.md §11). When enabled, hand off to
            // FinalQaController which shows the "Preparing questions…"
            // spinner and calls SessionLayer.BeginClarification. Otherwise
            // skip straight to evaluation.
            if (_sessionConfig != null && _sessionConfig.FinalQaEnabled && _finalQaController != null)
                _finalQaController.BeginFinalQAPhase();
            else
                BeginEvaluation();
        }

        public void BeginEvaluation()
        {
            _finalQaController?.OnEvaluationStarting();
            _evaluationProgressController?.BeginEvaluation();
            // Kick the AI's post-performance pipeline. The session layer enqueues
            // RunPostPerformancePipeline on its worker; module events drive the
            // evaluation progress panel via SessionLayer's evaluation events.
            _sessionLayer?.BeginEvaluation();
        }

        public async void CancelEvaluation()
        {
            // Order mirrors PHASE_5_TASKS §5.11.4: cancel inference → stop
            // audio → release echo mic → clean up session state → return to Lobby.
            _sessionLayer?.Cancel();
            _audioPipeline?.StopCapture();
            _echoProcessor?.Apply(false, 0f, _gameSettings?.MicrophoneDeviceName ?? "");

            if (_runtimeSlideSource != null)
            {
                Destroy(_runtimeSlideSource);
                _runtimeSlideSource = null;
            }

            _liveQaController?.OnSessionEnded();
            _finalQaController?.OnSessionEnded();

            _sessionConfig  = null;
            _gameSettings   = null;

            var gm = GameManager.Instance;
            if (gm != null) await gm.ReturnToLobby();

            // Restore Lobby state: flip ray mode so EndSessionController unbinds
            // the upper-button action (otherwise pressing B/Y in the Lobby would
            // re-open the End Session panel), and switch the watch back to its
            // wall-clock display.
            _rayController?.SetMode(RayMode.Lobby);
            var timer = FindAnyObjectByType<WristTimer>();
            timer?.SwitchToWallClock();
        }
    }
}
