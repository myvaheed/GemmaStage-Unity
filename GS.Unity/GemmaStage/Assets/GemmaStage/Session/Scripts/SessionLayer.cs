using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using GemmaStage.Boards;
using GemmaStage.Core;
using UnityEngine;

// Module type aliases. The Unity SessionLayer namespace (GemmaStage.Session)
// collides with several module type names (Session, SessionConfig,
// ClarificationResult, GroundTruthSummarizerResult, DeepDiveResult). Aliasing
// the module-side types keeps the Unity-side records (defined below) usable
// by their bare names while the module ones are unambiguous in this file.
using ModuleSession                     = GemmaStage.Session.Session;
using ModuleSessionConfig               = GemmaStage.Session.SessionConfig;
using ModuleSessionEvents               = GemmaStage.Session.SessionEvents;
using ModuleClarificationResult         = GemmaStage.Session.Clarification.ClarificationResult;
using ModuleGroundTruthSummarizerResult = GemmaStage.Session.GroundTruthSummarizer.GroundTruthSummarizerResult;
using ModuleGroundTruthDocInput         = GemmaStage.Session.GroundTruthSummarizer.GroundTruthDocInput;
using ModuleDeepDiveResult              = GemmaStage.Session.DeepDive.DeepDiveResult;
using ModuleMainIdeaComparatorResult    = GemmaStage.Session.MainIdeaComparator.MainIdeaComparatorResult;
using ModuleInquirerConcern             = GemmaStage.Session.Inquirer.InquirerConcern;
using ModuleInquirerConcernType         = GemmaStage.Session.Inquirer.InquirerConcernType;

namespace GemmaStage.Session
{
    // Unity-side bridge to the GemmaStage.Session C# module. Owns a single
    // worker thread that serialises every blocking module call (the engine
    // slot semaphore inside the module guarantees there is no point spinning
    // more workers) and marshals module events back onto the Unity main
    // thread via MainThreadDispatcher.
    //
    // Inspector wiring: lives on the SessionLayer GameObject in Shared.unity
    // alongside SessionManager. Model paths default to the repo's models/
    // folder; Phase 8 swaps these to StreamingAssets.
    public class SessionLayer : MonoBehaviour
    {
        [Header("Model paths (repo-relative, hard-coded for Phase 6)")]
        [Tooltip("GGUF path. In Editor: resolved as Application.dataPath/../../../<this> (sibling C++ repo). In a build: the suffix after \"models/\" is resolved against <gameRoot>/models/ (next to the .exe).")]
        [SerializeField] string _modelRelativePath  = "../GemmaStage/models/gemma-4-E4B/gemma-4-E4B-it-Q4_K_M.gguf";
        [Tooltip("mmproj path. In Editor: resolved as Application.dataPath/../../../<this> (sibling C++ repo). In a build: the suffix after \"models/\" is resolved against <gameRoot>/models/ (next to the .exe).")]
        [SerializeField] string _mmprojRelativePath = "../GemmaStage/models/gemma-4-E4B/mmproj-BF16.gguf";

        [Header("Logging")]
        [Tooltip("When ON, every parsed Conversation response (Perceptor transcripts, IdeaReflector retellings, Inquirer concern deltas, post-performance pipeline outputs) is logged to the Unity Console. Mirrors the PoC console output. Toggle at runtime via SessionLayer.VerboseLogging.")]
        [SerializeField] bool _verboseLogging = true;

        // Global gate for the in-flight parsed-response logger below. Mutable at
        // runtime so an external dev panel can toggle without touching the
        // inspector. Synchronised from `_verboseLogging` in Awake.
        public static bool VerboseLogging { get; set; } = true;

        [Header("XLSX Export")]
        [Tooltip("When ON, the full multi-sheet PoC-parity .xlsx is written at the end of RunPostPerformancePipeline. File lands under Application.persistentDataPath/<output dir>/ with a timestamped filename. Toggle at runtime via SessionLayer.XlsxExportEnabled.")]
        [SerializeField] bool _exportXlsx = true;
        [Tooltip("Subdirectory under Application.persistentDataPath where exported workbooks are written. Created on demand.")]
        [SerializeField] string _xlsxOutputDir = "SessionExports";

        public static bool XlsxExportEnabled { get; set; } = true;

        // ── Public state ─────────────────────────────────────────────────────
        public bool IsActive { get; private set; }
        public bool IsPaused { get; private set; }

        // Path to the most recently written PDF report. Set on the worker
        // thread from TryExportArtifacts and read on the main thread by the
        // Results screen's Save-as-PDF button. Volatile because the read
        // happens without a lock and we want callers to see the freshly
        // written value once the worker assigns it.
        public string LastExportedPdfPath
        {
            get => _lastExportedPdfPath;
            private set => _lastExportedPdfPath = value;
        }
        volatile string _lastExportedPdfPath;

        // ── Public event surface (stable since Phase 5) ──────────────────────
        public event Action<IReadOnlyList<LiveQaConcern>> OpenConcernsUpdated;
        public event Action<ClarificationResult>          ClarificationCompleted;
        public event Action                               TranscriptSummarizerMapCompleted;
        public event Action                               TranscriptSummarizerReduceCompleted;
        public event Action<GroundTruthSummarizerResult>  GroundTruthSummarizerCompleted;
        public event Action                               MainIdeaComparatorCompleted;
        public event Action<DeepDiveResult>               DeepDiveCompleted;
        // Fires after RunPostPerformancePipeline returns on the worker thread.
        // Bundles the projected DeepDiveResult with the auxiliary data the
        // Results panel needs (audience-inferred thesis, transcript-derived
        // inferred main idea, full per-chunk transcript with emotion/grammar).
        // Dispatched on the main thread.
        public event Action<SessionResultsPayload>        SessionResultsAvailable;
        // New in Phase 6 T8 — surfaces tool-call exhaustion failures so the
        // "Something went wrong" panel can show. (string role, string message)
        public event Action<string, string>               SessionFailed;

        // ── Internals ────────────────────────────────────────────────────────
        volatile ModuleSession _session;
        Thread _worker;
        BlockingCollection<Action> _queue;
        // Completes once StartSessionOnWorker finishes (true on success, false
        // on any pre-flight or native-init failure). Held as a field so the
        // worker, the SessionFailed path, and Cancel can all complete it
        // without leaking pending continuations. SessionManager awaits this to
        // gate the "Start Speech" popup behind a finished model load.
        TaskCompletionSource<bool> _startupTcs;
        // Flips to true the moment Cancel() starts. Worker checks it before
        // running each queued action and before surfacing exceptions, so the
        // tail of the queue (EndFinalQARound, RunPostPerformancePipeline, …)
        // doesn't fire SessionFailed onto a UI that's already transitioning
        // back to Lobby.
        volatile bool _cancelled;
        ModuleClarificationResult _clarification;
        // Captured from MainIdeaComparatorCompleted on the worker thread and
        // bundled into DeepDiveResult when DeepDiveCompleted fires immediately after.
        ModuleMainIdeaComparatorResult _lastComparatorResult;
        readonly List<BoardCapture> _boardCaptureSubs = new();
        AudioPipeline _audioPipeline;
        // Captured on the main thread in Begin and handed to SetDllDirectoryW
        // (Win32) so LoadLibrary can find GemmaStage.dll + its transitive deps
        // (llama.dll, mtmd.dll, ggml-*.dll) which all live here. The earlier
        // CWD-swap approach worked but was process-wide on Windows and
        // corrupted Unity Search indexing and other CWD-relative file I/O
        // even with a tight try/finally scope. SetDllDirectoryW is also
        // process-wide, but it only affects DLL search — file enumeration,
        // asset paths, and CWD-relative I/O are untouched.
        string _pluginsDir;
        // Absolute output directory captured on the main thread in Begin so the
        // worker can write the xlsx without touching Application.persistentDataPath
        // (a UnityEngine main-thread-only API).
        string _xlsxOutputDirAbsolute;
        static bool _dllSearchPathConfigured;

        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool SetDllDirectoryW(string lpPathName);

        void Awake()
        {
            if (GetComponent<MainThreadDispatcher>() == null)
                gameObject.AddComponent<MainThreadDispatcher>();
            VerboseLogging = _verboseLogging;
            XlsxExportEnabled = _exportXlsx;
        }

        void OnDestroy()
        {
            // Best-effort: drop in-flight inference and dispose native handles.
            Cancel();
        }

        // ── Begin: build module config + spin worker + kick Session.Start ───
        //
        // Returns a Task<bool> that completes once the native engine has
        // finished loading (true) or the startup failed (false). SessionManager
        // awaits this to keep the loading overlay up until the model is ready,
        // then surfaces the "Start Speech" popup behind it.
        public Task<bool> Begin(LobbySessionConfig config, GameSettings settings)
        {
            if (IsActive)
            {
                Debug.LogWarning("[SessionLayer] Begin called while already active. Ignoring.");
                return Task.FromResult(false);
            }
            IsActive = true;
            IsPaused = false;
            _cancelled = false;
            _clarification = null;
            LastExportedPdfPath = null;
            _startupTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            // Build module config on the main thread — Texture2D.EncodeToPNG
            // for ground-truth images is not thread-safe.
            var moduleConfig = BuildModuleConfig(config, settings);
            _pluginsDir = Path.Combine(Application.dataPath, "GemmaStage", "Plugins", "x86_64");
            _xlsxOutputDirAbsolute = string.IsNullOrWhiteSpace(_xlsxOutputDir)
                ? Application.persistentDataPath
                : Path.Combine(Application.persistentDataPath, _xlsxOutputDir);

            // Native EngineCreate just returns a null handle if the GGUF isn't
            // on disk — surface a clear error before the worker even spins.
            if (!File.Exists(moduleConfig.ModelPath))
            {
                IsActive = false;
                var msg = $"Model file not found: {moduleConfig.ModelPath}";
                Debug.LogError($"[SessionLayer] {msg}");
                SessionFailed?.Invoke("Session.Start", msg);
                _startupTcs.TrySetResult(false);
                return _startupTcs.Task;
            }
            if (!string.IsNullOrEmpty(moduleConfig.MmprojPath) && !File.Exists(moduleConfig.MmprojPath))
            {
                IsActive = false;
                var msg = $"mmproj file not found: {moduleConfig.MmprojPath}";
                Debug.LogError($"[SessionLayer] {msg}");
                SessionFailed?.Invoke("Session.Start", msg);
                _startupTcs.TrySetResult(false);
                return _startupTcs.Task;
            }

            _queue = new BlockingCollection<Action>();
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "GemmaStage.SessionLayer.Worker",
            };
            _worker.Start();

            // BoardCaptures live in the env scene which is already loaded by
            // the time SessionManager.StartSession reaches Begin. Audio
            // subscription is wired here too, but no chunks flow until
            // AudioPipeline.StartCapture is called by SessionManager after the
            // player presses Start Speech.
            SubscribeBoardCaptures();
            SubscribeAudioPipeline();

            // First job on the worker: bring the native engine up. Module
            // events are hooked once Session is constructed.
            _queue.Add(() => StartSessionOnWorker(moduleConfig));

            return _startupTcs.Task;
        }

        // ── Live-phase inputs (called from main thread, run on worker) ──────

        public void PushAudio(byte[] wav, TimeSpan duration)
        {
            if (!IsActive || IsPaused || wav == null || wav.Length == 0) return;
            EnqueueWorker("PushAudio", () => _session?.PushAudio(wav, duration));
        }

        public void PushImage(byte[] png)
        {
            if (!IsActive || png == null || png.Length == 0) return;
            EnqueueWorker("PushImage", () => _session?.PushImage(png));
        }

        // ── Live Q&A round entry / exit ──────────────────────────────────────

        public void BeginOnLiveQARound(LiveQaConcern concern)
        {
            if (!IsActive || concern == null) return;
            var moduleConcern = BuildModuleConcern(concern.Id, concern.Question, concern.Type);
            EnqueueWorker("BeginOnLiveQARound", () => _session?.BeginOnLiveQARound(moduleConcern));
        }

        public void EndOnLiveQARound()
        {
            if (!IsActive) return;
            EnqueueWorker("EndOnLiveQARound", () => _session?.EndOnLiveQARound());
        }

        // ── End live phase + post-performance sequence ──────────────────────

        public void End()
        {
            if (!IsActive) return;
            EnqueueWorker("EndLivePhase", () => _session?.EndLivePhase());
        }

        public void BeginClarification()
        {
            if (!IsActive) return;
            EnqueueWorker("RunClarification", () =>
            {
                var result = _session?.RunClarification();
                if (result == null) return;
                _clarification = result;
                _session?.LoadConcernsForFinalQA(result);
                // ClarificationCompleted fires from inside the module call
                // already — no manual dispatch needed here.
            });
        }

        public void BeginFinalQARound(FinalQaQuestion question)
        {
            if (!IsActive || question == null) return;
            var moduleConcern = BuildModuleConcern(question.Id, question.Question, question.Type);
            EnqueueWorker("BeginFinalQARound", () => _session?.BeginFinalQARound(moduleConcern));
        }

        public void EndFinalQARound()
        {
            if (!IsActive) return;
            EnqueueWorker("EndFinalQARound", () => _session?.EndFinalQARound());
        }

        public void BeginEvaluation()
        {
            if (!IsActive) return;
            EnqueueWorker("RunPostPerformancePipeline", () =>
            {
                var session = _session;
                if (session == null) return;
                var post = session.RunPostPerformancePipeline(_clarification);

                // Build + fire the rich results payload BEFORE export so the UI
                // doesn't wait for XLSX/PDF generation to finish before showing
                // the panel. Export continues on this same worker after the
                // dispatch.
                if (post != null)
                {
                    var payload = BuildSessionResultsPayload(session, post);
                    DispatchEvent(() => SessionResultsAvailable?.Invoke(payload));
                }

                // Same worker thread — ClosedXML/QuestPDF are purely CPU/IO;
                // the session stores (`TranscriptStore`, `RetellingsHistory`,
                // …) are already serialised by this worker's earlier work so
                // reading them here is safe.
                if (XlsxExportEnabled && post != null)
                    TryExportArtifacts(session, post);
            });
        }

        // ── XLSX + PDF export ────────────────────────────────────────────────

        void TryExportArtifacts(ModuleSession session, GemmaStage.Session.PostPerformanceResult post)
        {
            var outDir = _xlsxOutputDirAbsolute;
            if (string.IsNullOrEmpty(outDir))
            {
                MainThreadDispatcher.Enqueue(() =>
                    Debug.LogWarning("[SessionLayer] Export skipped — output dir not initialised."));
                return;
            }

            try
            {
                Directory.CreateDirectory(outDir);
                var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
                var xlsxPath = Path.Combine(outDir, $"session-{stamp}.xlsx");
                var pdfPath  = Path.Combine(outDir, $"session-{stamp}.pdf");

                GemmaStage.Session.Export.XlsxExporter.Export(
                    path:                   xlsxPath,
                    session:                session,
                    summarizerResult:       post.TranscriptSummarizer,
                    clarificationResult:    _clarification,
                    deepDiveResult:         post.DeepDive?.Result,
                    mainIdeaComparator:     post.MainIdeaComparator,
                    groundTruthResult:      post.GroundTruthSummarizer,
                    memSamples:             null);

                MainThreadDispatcher.Enqueue(() =>
                    Debug.Log($"[SessionLayer] XLSX written: {xlsxPath}"));

                try
                {
                    GemmaStage.Session.Export.PdfExporter.Export(
                        path:                pdfPath,
                        session:             session,
                        deepDiveResult:      post.DeepDive?.Result,
                        clarificationResult: _clarification,
                        mainIdeaComparator:  post.MainIdeaComparator,
                        backendName:         session.BackendName,
                        sessionEndedAt:      DateTimeOffset.Now);
                    LastExportedPdfPath = pdfPath;
                    MainThreadDispatcher.Enqueue(() =>
                        Debug.Log($"[SessionLayer] PDF written: {pdfPath}"));
                }
                catch (Exception pdfEx)
                {
                    // Walk the inner-exception chain so the real cause
                    // surfaces in the Unity Console (TypeInitializationException
                    // and friends bury the meaningful message behind a
                    // generic outer wrapper).
                    var detail = FlattenExceptionChain(pdfEx);
                    var trace = pdfEx.ToString();
                    MainThreadDispatcher.Enqueue(() =>
                    {
                        Debug.LogWarning($"[SessionLayer] PDF export failed: {detail}");
                        Debug.LogWarning($"[SessionLayer] PDF export stack:\n{trace}");
                    });
                }
            }
            catch (Exception ex)
            {
                var msg = ex.Message;
                MainThreadDispatcher.Enqueue(() =>
                    Debug.LogWarning($"[SessionLayer] Export failed: {msg}"));
            }
        }

        // Flattens an exception chain into "Outer: msg | Inner: msg | …" so a
        // single LogWarning shows the real cause even when wrapped multiple
        // times (TypeInitializationException → FileNotFoundException, etc.).
        static string FlattenExceptionChain(Exception ex)
        {
            var parts = new List<string>();
            var cur = ex;
            int safety = 8;
            while (cur != null && safety-- > 0)
            {
                parts.Add($"{cur.GetType().Name}: {cur.Message}");
                cur = cur.InnerException;
            }
            return string.Join(" | ", parts);
        }

        // ── Pause / Resume — gates PushAudio. Mic + native engine stay open. ─

        public void PauseLivePhase()
        {
            IsPaused = true;
            // Stop the chunker from running over fresh mic samples so a long
            // pause doesn't flush a big chunk at resume. Mic device stays open.
            _audioPipeline?.Pause();
        }

        public void ResumeLivePhase()
        {
            IsPaused = false;
            _audioPipeline?.Resume();
        }

        // ── Cancel: stop in-flight inference, drain worker, dispose ─────────

        public void Cancel()
        {
            if (!IsActive) return;
            // Set _cancelled before IsActive so any concurrent worker action
            // that re-checks state sees the cancellation. Both flips are on the
            // main thread so the ordering matters only for the worker reads.
            _cancelled = true;
            IsActive = false;

            // Stop receiving fresh chunks/images. Subscribers are removed
            // before the engine starts tearing down so a late chunk doesn't
            // race a half-disposed session.
            UnsubscribeBoardCaptures();
            UnsubscribeAudioPipeline();

            // Signal the queue closed first so any worker action that wakes
            // mid-Cancel doesn't enqueue follow-up work.
            try { _queue?.CompleteAdding(); }
            catch { /* already disposed */ }

            // Module's Cancel(): signals abort, waits for the in-flight
            // conversation slot to drain, then disposes the engine. BLOCKING
            // — main thread parks here for up to a few hundred ms while the
            // current native turn aborts. Module.Dispose is internal to
            // Session.Cancel, so no separate _session.Dispose() call below.
            try { _session?.Cancel(); }
            catch (Exception ex) { Debug.LogException(ex); }

            // Drain the worker. CompleteAdding above plus the in-loop
            // _cancelled check means each remaining queued action is a no-op,
            // so this returns quickly.
            try { _worker?.Join(TimeSpan.FromSeconds(5)); }
            catch (Exception ex) { Debug.LogException(ex); }
            _worker = null;
            _session = null;

            try { _queue?.Dispose(); }
            catch { /* swallow */ }
            _queue = null;

            _clarification        = null;
            _lastComparatorResult = null;
            // If Cancel races a still-running StartSessionOnWorker (rare —
            // covers the case where the user backs out during EngineCreate),
            // unblock SessionManager's await on Begin so it doesn't hang.
            _startupTcs?.TrySetResult(false);
            _startupTcs = null;
        }

        // ── Worker thread ────────────────────────────────────────────────────

        void StartSessionOnWorker(ModuleSessionConfig moduleConfig)
        {
            var tcs = _startupTcs;
            try
            {
                EnsureNativeDllSearchPath();

                var constructed = ModuleSession.Start(moduleConfig, warn: msg =>
                    MainThreadDispatcher.Enqueue(() => Debug.Log($"[GemmaStage.Session warn] {msg}")));

                // User may have clicked Cancel while EngineCreate was running.
                // The cancel path can't see _session yet (we publish it below),
                // so dispose locally and bail before any events are hooked.
                if (_cancelled)
                {
                    try { constructed.Dispose(); } catch (Exception ex) { Debug.LogException(ex); }
                    tcs?.TrySetResult(false);
                    return;
                }

                _session = constructed;
                var backend = _session.BackendName;
                MainThreadDispatcher.Enqueue(() =>
                    Debug.Log($"[SessionLayer] Session started — backend={backend}, model={moduleConfig.ModelPath}"));
                HookModuleEvents(_session.Events);
                tcs?.TrySetResult(true);
            }
            catch (Exception ex)
            {
                var message = ex.Message;
                DispatchEvent(() =>
                {
                    Debug.LogError($"[SessionLayer] Session.Start failed: {ex}");
                    SessionFailed?.Invoke("Session.Start", message);
                });
                tcs?.TrySetResult(false);
            }
        }

        // Marshals a Unity-side event invocation to the main thread. Drops the
        // call (both at enqueue time and at dispatch time) once Cancel() has
        // flipped _cancelled, so a tail of queued module events doesn't fire
        // OpenConcernsUpdated / ClarificationCompleted / TranscriptSummarizer*
        // / MainIdeaComparatorCompleted / DeepDiveCompleted / SessionFailed
        // onto a UI that's already transitioning back to Lobby.
        //
        // The double-check covers the race where the worker reads _cancelled
        // as false, enqueues, and only then Cancel() flips it.
        void DispatchEvent(Action action)
        {
            if (action == null) return;
            if (_cancelled) return;
            MainThreadDispatcher.Enqueue(() =>
            {
                if (_cancelled) return;
                action();
            });
        }

        // Adds Plugins/x86_64 to the Win32 LoadLibrary search path so
        // DllImport("GemmaStage") resolves (the bare-GUID .meta files don't
        // register the DLLs as Editor native plugins) and so the engine's
        // dynamic ggml backend discovery picks up the ggml-*.dll siblings.
        // Static guard — SetDllDirectory is process-wide, one call is enough.
        void EnsureNativeDllSearchPath()
        {
            if (_dllSearchPathConfigured) return;
            if (string.IsNullOrEmpty(_pluginsDir) || !Directory.Exists(_pluginsDir))
            {
                MainThreadDispatcher.Enqueue(() =>
                    Debug.LogWarning($"[SessionLayer] Plugins dir missing — native DLLs may not load: {_pluginsDir}"));
                return;
            }
            if (!SetDllDirectoryW(_pluginsDir))
            {
                var err = Marshal.GetLastWin32Error();
                MainThreadDispatcher.Enqueue(() =>
                    Debug.LogWarning($"[SessionLayer] SetDllDirectoryW failed (err={err}) for {_pluginsDir}"));
                return;
            }
            _dllSearchPathConfigured = true;
        }

        void WorkerLoop()
        {
            try
            {
                foreach (var action in _queue.GetConsumingEnumerable())
                {
                    try { action?.Invoke(); }
                    catch (Exception ex) { Debug.LogException(ex); }
                }
            }
            catch (Exception ex) { Debug.LogException(ex); }
        }

        void EnqueueWorker(string stage, Action body)
        {
            var q = _queue;
            if (q == null || q.IsAddingCompleted) return;
            try
            {
                q.Add(() =>
                {
                    // Cancel() flips _cancelled before draining the queue.
                    // Skip the body so EndFinalQARound / RunPostPerformancePipeline /
                    // … queued before Cancel don't run against a torn-down
                    // engine. DispatchEvent below additionally swallows the
                    // SessionFailed surfacing if the in-flight body throws.
                    if (_cancelled) return;
                    try { body(); }
                    catch (Exception ex)
                    {
                        var msg = ex.Message;
                        DispatchEvent(() =>
                        {
                            Debug.LogError($"[SessionLayer] {stage} threw: {ex}");
                            SessionFailed?.Invoke(stage, msg);
                        });
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // CompleteAdding raced with us. Drop the work silently.
            }
        }

        // ── Module → Unity event marshalling ────────────────────────────────

        void HookModuleEvents(ModuleSessionEvents events)
        {
            // Verbose parsed-response logger. Each handler checks
            // SessionLayer.VerboseLogging internally so toggling the static
            // at runtime takes effect immediately. Pure observer — does not
            // touch any Unity-side state.
            events.PerceptorTurnCompleted           += SessionVerboseLogger.LogPerceptorTurn;
            events.CycleStarting                    += SessionVerboseLogger.LogCycleStarting;
            events.IdeaReflectorTurnCompleted       += SessionVerboseLogger.LogIdeaReflectorTurn;
            events.InquirerTurnCompleted            += SessionVerboseLogger.LogInquirerTurn;
            events.CycleCompleted                   += SessionVerboseLogger.LogCycleCompleted;
            events.SessionEnded                     += SessionVerboseLogger.LogSessionEnded;
            events.TranscriptSummarizerMapCompleted    += SessionVerboseLogger.LogTranscriptSummarizerMap;
            events.TranscriptSummarizerReduceCompleted += SessionVerboseLogger.LogTranscriptSummarizerReduce;
            events.GroundTruthSummarizerCompleted   += SessionVerboseLogger.LogGroundTruthSummarizer;
            events.MainIdeaComparatorCompleted      += SessionVerboseLogger.LogMainIdeaComparator;
            events.ClarificationCompleted           += SessionVerboseLogger.LogClarification;
            events.DeepDiveSubRoleCompleted         += SessionVerboseLogger.LogDeepDiveSubRole;
            events.DeepDiveCompleted                += SessionVerboseLogger.LogDeepDive;

            events.OpenConcernsUpdated += payload =>
            {
                var projected = ProjectConcerns(payload.OpenConcerns);
                DispatchEvent(() => OpenConcernsUpdated?.Invoke(projected));
            };

            events.ClarificationCompleted += result =>
            {
                var projected = ProjectClarification(result);
                DispatchEvent(() => ClarificationCompleted?.Invoke(projected));
            };

            events.TranscriptSummarizerMapCompleted += _ =>
                DispatchEvent(() => TranscriptSummarizerMapCompleted?.Invoke());

            events.TranscriptSummarizerReduceCompleted += _ =>
                DispatchEvent(() => TranscriptSummarizerReduceCompleted?.Invoke());

            events.GroundTruthSummarizerCompleted += result =>
            {
                var projected = new GroundTruthSummarizerResult(result.Ran);
                DispatchEvent(() => GroundTruthSummarizerCompleted?.Invoke(projected));
            };

            events.MainIdeaComparatorCompleted += result =>
            {
                // Capture on the worker thread — DeepDiveCompleted fires next in the
                // same sequential post-performance pipeline, so no lock is needed.
                _lastComparatorResult = result;
                DispatchEvent(() => MainIdeaComparatorCompleted?.Invoke());
            };

            events.DeepDiveCompleted += moduleResult =>
            {
                var cmp      = _lastComparatorResult;
                var projected = ProjectDeepDiveResult(moduleResult, cmp);
                DispatchEvent(() => DeepDiveCompleted?.Invoke(projected));
            };

            events.SessionFailed += failure =>
            {
                var role = failure.Role;
                var msg = failure.Message;
                DispatchEvent(() =>
                {
                    Debug.LogError($"[SessionLayer] SessionFailed in {role}: {msg}");
                    SessionFailed?.Invoke(role, msg);
                });
            };
        }

        // ── Projections (module records → Unity-side records) ────────────────

        static IReadOnlyList<LiveQaConcern> ProjectConcerns(IReadOnlyList<ModuleInquirerConcern> moduleConcerns)
        {
            if (moduleConcerns == null || moduleConcerns.Count == 0)
                return Array.Empty<LiveQaConcern>();
            var arr = new LiveQaConcern[moduleConcerns.Count];
            for (int i = 0; i < moduleConcerns.Count; i++)
            {
                var c = moduleConcerns[i];
                arr[i] = new LiveQaConcern(c.Id, c.Question, MapToUnity(c.Type));
            }
            return arr;
        }

        static DeepDiveResult ProjectDeepDiveResult(
            ModuleDeepDiveResult m,
            ModuleMainIdeaComparatorResult cmpResult)
        {
            ResultComparatorData comparator = null;
            if (cmpResult?.Output is { } o)
            {
                var coverages = new ResultClaimCoverage[o.ClaimCoverages.Count];
                for (int i = 0; i < o.ClaimCoverages.Count; i++)
                {
                    var c      = o.ClaimCoverages[i];
                    var label  = c.Coverage.ToString().ToLowerInvariant();
                    coverages[i] = new ResultClaimCoverage(c.AnchorClaim, label, c.Evidence);
                }
                comparator = new ResultComparatorData(
                    o.AnchorThesis, o.AudienceThesis, o.ThesisComparison, coverages, o.Recall);
            }
            return new DeepDiveResult(
                MapCrit(m.MainIdeaClarity),
                MapCrit(m.Structure),
                MapCrit(m.ConsistencyFocus),
                MapCrit(m.SupportJustification),
                MapCrit(m.LanguageQuality),
                MapCrit(m.EmotionalDelivery),
                MapCrit(m.QaHandling),
                comparator);

            static ResultCriterion MapCrit(GemmaStage.Session.DeepDive.DeepDiveCriterion c)
                => new ResultCriterion((int)c.Value, c.Verdict);
        }

        // Bundles the deep-dive projection with the auxiliary post-performance
        // outputs the Results panel needs. Called on the worker thread inside
        // the same EnqueueWorker body that just produced `post`, so all reads
        // are guaranteed to see a fully-populated pipeline.
        SessionResultsPayload BuildSessionResultsPayload(
            ModuleSession session,
            GemmaStage.Session.PostPerformanceResult post)
        {
            var deepDive = post.DeepDive?.Result is { } moduleDeep
                ? ProjectDeepDiveResult(moduleDeep, _lastComparatorResult)
                : null;

            var inferredMainIdea =
                post.TranscriptSummarizer?.ReduceOutput?.InferredMainIdeaFromTranscript;
            var audienceThesis = post.IdeaComprehension?.Parse?.Output?.Thesis;

            var transcript = BuildTranscriptRecords(session);

            DeepDiveDetailsBundle details = null;
            if (post.DeepDive?.Inputs is { } inputs)
                details = ProjectDeepDiveDetails(inputs);

            return new SessionResultsPayload(deepDive, inferredMainIdea, audienceThesis, transcript, details);
        }

        static DeepDiveDetailsBundle ProjectDeepDiveDetails(GemmaStage.Session.DeepDive.DeepDiveSubInputs inputs)
        {
            var structure        = ProjectStructureLike(inputs.Structure.Rows,            inputs.Structure.Computed);
            var consistency      = ProjectConsistencyLike(inputs.ConsistencyFocus.Rows,   inputs.ConsistencyFocus.Computed);
            var support          = ProjectSupportLike(inputs.SupportJustification.Rows,  inputs.SupportJustification.Computed);
            var language         = ProjectLanguage(inputs.LanguageQuality);
            var emotional        = ProjectEmotional(inputs.EmotionalDelivery);
            var qa               = ProjectQa(inputs.QaHandling);
            return new DeepDiveDetailsBundle(structure, consistency, support, language, emotional, qa);
        }

        static DeepDiveQaDetails ProjectQa(GemmaStage.Session.DeepDive.QaHandling.QaHandlingInput input)
        {
            var src = input.Spans;
            if (src == null || src.Count == 0) return null;
            var arr = new DeepDiveQaSpan[src.Count];
            for (int i = 0; i < src.Count; i++)
            {
                var s = src[i];
                arr[i] = new DeepDiveQaSpan(
                    phase:      s.Phase == GemmaStage.Session.Stores.QaPhase.Live ? "Live" : "Final",
                    question:   s.QuestionText,
                    answer:     s.AnswerText,
                    resolution: s.Resolution == GemmaStage.Session.DeepDive.QaResolution.Resolved ? "resolved" : "not answered");
            }
            return new DeepDiveQaDetails(arr);
        }

        static DeepDiveScoreSliceDetails ProjectStructureLike(
            IReadOnlyList<GemmaStage.Session.DeepDive.StructureChunkRow> rows,
            GemmaStage.Session.DeepDive.ComputedScore computed)
        {
            var arr = new DeepDiveChunkRow[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                arr[i] = new DeepDiveChunkRow(rows[i].Index, rows[i].Retelling, rows[i].StructureLabel);
            return new DeepDiveScoreSliceDetails(arr, computed.Value, computed.Label);
        }

        static DeepDiveScoreSliceDetails ProjectConsistencyLike(
            IReadOnlyList<GemmaStage.Session.DeepDive.ConsistencyChunkRow> rows,
            GemmaStage.Session.DeepDive.ComputedScore computed)
        {
            var arr = new DeepDiveChunkRow[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                arr[i] = new DeepDiveChunkRow(rows[i].Index, rows[i].Retelling, rows[i].ConsistencyLabel);
            return new DeepDiveScoreSliceDetails(arr, computed.Value, computed.Label);
        }

        static DeepDiveScoreSliceDetails ProjectSupportLike(
            IReadOnlyList<GemmaStage.Session.DeepDive.SupportChunkRow> rows,
            GemmaStage.Session.DeepDive.ComputedScore computed)
        {
            var arr = new DeepDiveChunkRow[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                arr[i] = new DeepDiveChunkRow(rows[i].Index, rows[i].Retelling, rows[i].SupportLabel);
            return new DeepDiveScoreSliceDetails(arr, computed.Value, computed.Label);
        }

        static DeepDiveLanguageDetails ProjectLanguage(
            GemmaStage.Session.DeepDive.LanguageQuality.LanguageQualityInput input)
        {
            var slices = new DeepDiveSliceLine[input.WeakSlices.Count];
            for (int i = 0; i < input.WeakSlices.Count; i++)
                slices[i] = new DeepDiveSliceLine(input.WeakSlices[i].Sequence, input.WeakSlices[i].Text);

            var dist  = ProjectDistribution(input.Distribution.distribution);
            var notes = new DeepDiveDynamicsNote[input.Distribution.notes.Count];
            for (int i = 0; i < input.Distribution.notes.Count; i++)
                notes[i] = new DeepDiveDynamicsNote(input.Distribution.notes[i].grammar, input.Distribution.notes[i].transcript);

            return new DeepDiveLanguageDetails(
                slices, input.Computed.value, input.Computed.label,
                input.Distribution.overall, dist, notes);
        }

        static DeepDiveEmotionDetails ProjectEmotional(
            GemmaStage.Session.DeepDive.EmotionalDelivery.EmotionalDeliveryInput input)
        {
            var dist  = ProjectDistribution(input.Dynamics.distribution);
            var notes = new DeepDiveDynamicsNote[input.Dynamics.notes.Count];
            for (int i = 0; i < input.Dynamics.notes.Count; i++)
                notes[i] = new DeepDiveDynamicsNote(input.Dynamics.notes[i].emotion, input.Dynamics.notes[i].transcript);

            return new DeepDiveEmotionDetails(input.Dynamics.overall, dist, notes);
        }

        static DeepDiveDistributionBucket[] ProjectDistribution(IReadOnlyDictionary<string, int> dist)
        {
            if (dist == null || dist.Count == 0) return Array.Empty<DeepDiveDistributionBucket>();
            var arr = new DeepDiveDistributionBucket[dist.Count];
            int i = 0;
            foreach (var kv in dist) arr[i++] = new DeepDiveDistributionBucket(kv.Key, kv.Value);
            return arr;
        }

        // Zips TranscriptStore + MetricsStore by Sequence (Sequence is the
        // monotonic TurnSequence each AsrConversation turn was tagged with —
        // both stores see the same value for the same turn). Offset is taken
        // relative to the first transcript entry so the table starts at 00:00
        // regardless of session wall-clock start.
        static IReadOnlyList<TranscriptChunkRecord> BuildTranscriptRecords(ModuleSession session)
        {
            var transcripts = session.TranscriptStore.Snapshot();
            if (transcripts.Count == 0)
                return Array.Empty<TranscriptChunkRecord>();

            var metrics = session.MetricsStore.Snapshot();
            var metricBySeq = new Dictionary<long, GemmaStage.Session.Stores.MetricEntry>(metrics.Count);
            for (int i = 0; i < metrics.Count; i++)
                metricBySeq[metrics[i].Sequence] = metrics[i];

            var anchor = transcripts[0].Timestamp;
            var arr = new TranscriptChunkRecord[transcripts.Count];
            for (int i = 0; i < transcripts.Count; i++)
            {
                var t = transcripts[i];
                var offset = t.Timestamp - anchor;
                metricBySeq.TryGetValue(t.Sequence, out var m);
                arr[i] = new TranscriptChunkRecord(
                    i + 1,
                    offset,
                    t.Text ?? string.Empty,
                    m?.Emotion,
                    m?.Grammar);
            }
            return arr;
        }

        static ClarificationResult ProjectClarification(ModuleClarificationResult result)
        {
            var unresolved = result?.UnresolvedQuestions;
            if (unresolved == null || unresolved.Count == 0)
                return new ClarificationResult(Array.Empty<FinalQaQuestion>());
            var arr = new FinalQaQuestion[unresolved.Count];
            for (int i = 0; i < unresolved.Count; i++)
            {
                var q = unresolved[i];
                arr[i] = new FinalQaQuestion(q.Id, q.Question, MapToUnity(q.Type));
            }
            return new ClarificationResult(arr);
        }

        static ModuleInquirerConcern BuildModuleConcern(long id, string question, ConcernType type)
            => new ModuleInquirerConcern(id, question, MapToModule(type));

        static ConcernType MapToUnity(ModuleInquirerConcernType t) => t switch
        {
            ModuleInquirerConcernType.TopicUnknown     => ConcernType.TopicUnknown,
            ModuleInquirerConcernType.ComprehensionGap => ConcernType.ComprehensionGap,
            ModuleInquirerConcernType.DetailRequest    => ConcernType.DetailRequest,
            _                                          => ConcernType.ComprehensionGap,
        };

        static ModuleInquirerConcernType MapToModule(ConcernType t) => t switch
        {
            ConcernType.TopicUnknown     => ModuleInquirerConcernType.TopicUnknown,
            ConcernType.ComprehensionGap => ModuleInquirerConcernType.ComprehensionGap,
            ConcernType.DetailRequest    => ModuleInquirerConcernType.DetailRequest,
            _                            => ModuleInquirerConcernType.ComprehensionGap,
        };

        // ── Module config builder ────────────────────────────────────────────

        ModuleSessionConfig BuildModuleConfig(LobbySessionConfig unityConfig, GameSettings settings)
        {
            var modelPath  = ResolveModelPath(_modelRelativePath);
            var mmprojPath = ResolveModelPath(_mmprojRelativePath);

            ModuleGroundTruthDocInput gtInput = ModuleGroundTruthDocInput.None;
            switch (unityConfig.GroundTruthKind)
            {
                case GroundTruthAttachmentKind.Text:
                    if (!string.IsNullOrEmpty(unityConfig.GroundTruthDocPath))
                        gtInput = ModuleGroundTruthDocInput.FilePath(unityConfig.GroundTruthDocPath);
                    break;
                case GroundTruthAttachmentKind.Image:
                case GroundTruthAttachmentKind.Pdf:
                    if (unityConfig.GroundTruthRenderedTexture != null)
                    {
                        var png = unityConfig.GroundTruthRenderedTexture.EncodeToPNG();
                        if (png != null && png.Length > 0)
                            gtInput = ModuleGroundTruthDocInput.RasterizedImageBytes(png);
                    }
                    break;
            }

            return new ModuleSessionConfig(
                ModelPath:        modelPath,
                MmprojPath:       mmprojPath,
                Language:         (settings?.Language ?? LanguageCode.English).ToString(),
                EnableLiveQA:     unityConfig.LiveQaEnabled,
                EnableFinalQA:    unityConfig.FinalQaEnabled,
                TimeLimit:        TimeSpan.FromMinutes(unityConfig.DurationMinutes),
                GroundTruthInput: gtInput);
        }

        // Editor: keep the legacy resolution (relative to repo root, so the
        // sibling GemmaStage/ C++ repo's models/ folder is reachable).
        // Build: anchor at <gameRoot>/models/ (sibling of the .exe) and take
        // the suffix of the configured path after the last "models/" segment.
        // This lets a packaged build ship models in a top-level models/ folder
        // without any inspector changes.
        static string ResolveModelPath(string configured)
        {
            if (Application.isEditor)
            {
                var repoRoot = Path.GetFullPath(Path.Combine(Application.dataPath, "..", "..", ".."));
                return Path.GetFullPath(Path.Combine(repoRoot, configured));
            }

            var gameRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var normalized = configured.Replace('\\', '/');
            var idx = normalized.LastIndexOf("models/", StringComparison.OrdinalIgnoreCase);
            var suffix = idx >= 0 ? normalized.Substring(idx + "models/".Length) : Path.GetFileName(normalized);
            return Path.GetFullPath(Path.Combine(gameRoot, "models", suffix));
        }

        // ── BoardCapture subscription ────────────────────────────────────────

        void SubscribeBoardCaptures()
        {
            _boardCaptureSubs.Clear();
            var caps = UnityEngine.Object.FindObjectsByType<BoardCapture>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            foreach (var cap in caps)
            {
                if (cap == null) continue;
                cap.OnCaptureRequested += HandleBoardCapture;
                _boardCaptureSubs.Add(cap);
            }
        }

        void UnsubscribeBoardCaptures()
        {
            foreach (var cap in _boardCaptureSubs)
            {
                if (cap != null) cap.OnCaptureRequested -= HandleBoardCapture;
            }
            _boardCaptureSubs.Clear();
        }

        void HandleBoardCapture(BoardCapture.BoardKind kind, byte[] png)
        {
            if (png == null || png.Length == 0) return;
            PushImage(png);
        }

        // ── AudioPipeline subscription ───────────────────────────────────────

        void SubscribeAudioPipeline()
        {
            if (_audioPipeline == null)
                _audioPipeline = UnityEngine.Object.FindAnyObjectByType<AudioPipeline>();
            if (_audioPipeline == null)
            {
                Debug.LogWarning("[SessionLayer] No AudioPipeline found — live audio won't reach the session.");
                return;
            }
            _audioPipeline.ChunkReady += HandleAudioChunkReady;
        }

        void UnsubscribeAudioPipeline()
        {
            if (_audioPipeline == null) return;
            _audioPipeline.ChunkReady -= HandleAudioChunkReady;
            _audioPipeline = null;
        }

        void HandleAudioChunkReady(byte[] wav, TimeSpan duration)
        {
            // Uncomment to verify the mic→classifier→chunker pipeline without
            // depending on the model being loaded.
            Debug.Log($"[SessionLayer] audio chunk {wav.Length} bytes / {duration.TotalSeconds:0.00}s");
            PushAudio(wav, duration);
        }

        // ── Dev / test hooks (Phase 5 DevSignals path — kept unchanged) ─────

        internal void RaiseOpenConcernsUpdated(IReadOnlyList<LiveQaConcern> concerns)         => OpenConcernsUpdated?.Invoke(concerns);
        internal void RaiseClarificationCompleted(ClarificationResult result)                 => ClarificationCompleted?.Invoke(result);
        internal void RaiseTranscriptSummarizerMapCompleted()                                 => TranscriptSummarizerMapCompleted?.Invoke();
        internal void RaiseTranscriptSummarizerReduceCompleted()                              => TranscriptSummarizerReduceCompleted?.Invoke();
        internal void RaiseGroundTruthSummarizerCompleted(GroundTruthSummarizerResult r)      => GroundTruthSummarizerCompleted?.Invoke(r);
        internal void RaiseMainIdeaComparatorCompleted()                                      => MainIdeaComparatorCompleted?.Invoke();
        internal void RaiseDeepDiveCompleted(DeepDiveResult r)                                => DeepDiveCompleted?.Invoke(r);
        internal void RaiseSessionResultsAvailable(SessionResultsPayload p)                   => SessionResultsAvailable?.Invoke(p);
    }

    // ─── Unity-facing record types (referenced by LiveQaController, FinalQaController, EvaluationProgressController, …) ───

    // Unity-side mirror of GemmaStage.Session.Inquirer.InquirerConcernType.
    public enum ConcernType
    {
        TopicUnknown = 0,
        ComprehensionGap = 1,
        DetailRequest = 2,
    }

    // Concern surfaced during the live phase. Carries id + type so the round
    // can be opened with the originating concern identity, not just its text.
    public sealed class LiveQaConcern
    {
        public long Id { get; }
        public string Question { get; }
        public ConcernType Type { get; }

        public LiveQaConcern(long id, string question, ConcernType type)
        {
            Id = id;
            Question = question;
            Type = type;
        }
    }

    // Final Q&A question surfaced after Clarification's Revise + Resolve walk.
    public sealed class FinalQaQuestion
    {
        public long Id { get; }
        public string Question { get; }
        public ConcernType Type { get; }

        public FinalQaQuestion(long id, string question, ConcernType type)
        {
            Id = id;
            Question = question;
            Type = type;
        }
    }

    // Unity-side Clarification carrier. UnresolvedQuestions drive the Final
    // Q&A panel sequence.
    public sealed class ClarificationResult
    {
        public IReadOnlyList<FinalQaQuestion> UnresolvedQuestions { get; }

        public ClarificationResult(IReadOnlyList<FinalQaQuestion> unresolvedQuestions)
        {
            UnresolvedQuestions = unresolvedQuestions ?? Array.Empty<FinalQaQuestion>();
        }
    }

    // GroundTruthSummarizer stage result. Carries only the Ran flag — the
    // anchor thesis and claims are bundled into DeepDiveResult.Comparator.
    public sealed class GroundTruthSummarizerResult
    {
        public bool Ran { get; }
        public GroundTruthSummarizerResult(bool ran) { Ran = ran; }
    }

    // Score + verdict for one DeepDive criterion. Value is 0 (N/A) or 1–5.
    public sealed class ResultCriterion
    {
        public int    Value   { get; }
        public string Verdict { get; }
        public ResultCriterion(int value, string verdict) { Value = value; Verdict = verdict ?? ""; }
    }

    // Per-claim coverage entry from MainIdeaComparator.
    public sealed class ResultClaimCoverage
    {
        public string Claim    { get; }
        public string Coverage { get; }  // "yes" | "partial" | "no"
        public string Evidence { get; }  // may be null or empty
        public ResultClaimCoverage(string claim, string coverage, string evidence)
        { Claim = claim ?? ""; Coverage = coverage ?? ""; Evidence = evidence; }
    }

    // Thesis-level and per-claim comparator output. Null on DeepDiveResult when
    // no ground-truth document was attached for this session.
    public sealed class ResultComparatorData
    {
        public string                          AnchorThesis     { get; }
        public string                          AudienceThesis   { get; }
        public string                          ThesisComparison { get; }
        public IReadOnlyList<ResultClaimCoverage> ClaimCoverages { get; }
        public double                          Recall           { get; }

        public ResultComparatorData(
            string anchorThesis, string audienceThesis, string thesisComparison,
            IReadOnlyList<ResultClaimCoverage> claimCoverages, double recall)
        {
            AnchorThesis     = anchorThesis     ?? "";
            AudienceThesis   = audienceThesis   ?? "";
            ThesisComparison = thesisComparison ?? "";
            ClaimCoverages   = claimCoverages   ?? Array.Empty<ResultClaimCoverage>();
            Recall           = recall;
        }
    }

    // Full DeepDive evaluation result surfaced to Unity after the post-performance
    // pipeline completes. Seven per-criterion records (Value 0 = N/A, 1–5 scored)
    // plus an optional comparator section that is non-null only when a ground-truth
    // document was attached for the session.
    public sealed class DeepDiveResult
    {
        public ResultCriterion      MainIdeaClarity      { get; }
        public ResultCriterion      Structure            { get; }
        public ResultCriterion      ConsistencyFocus     { get; }
        public ResultCriterion      SupportJustification { get; }
        public ResultCriterion      LanguageQuality      { get; }
        public ResultCriterion      EmotionalDelivery    { get; }
        public ResultCriterion      QaHandling           { get; }
        public ResultComparatorData Comparator           { get; }  // null when no GT

        public DeepDiveResult(
            ResultCriterion      mainIdeaClarity,
            ResultCriterion      structure,
            ResultCriterion      consistencyFocus,
            ResultCriterion      supportJustification,
            ResultCriterion      languageQuality,
            ResultCriterion      emotionalDelivery,
            ResultCriterion      qaHandling,
            ResultComparatorData comparator)
        {
            MainIdeaClarity      = mainIdeaClarity;
            Structure            = structure;
            ConsistencyFocus     = consistencyFocus;
            SupportJustification = supportJustification;
            LanguageQuality      = languageQuality;
            EmotionalDelivery    = emotionalDelivery;
            QaHandling           = qaHandling;
            Comparator           = comparator;
        }

        // Dev factory — creates plausible dummy data for smoke-testing the
        // Results UI without running the full inference pipeline.
        public static DeepDiveResult ForDev(bool withComparator = false)
        {
            var cmp = withComparator ? new ResultComparatorData(
                anchorThesis:     "Sleep is foundational to health and cognitive performance.",
                audienceThesis:   "Sleep helps memory and immune function.",
                thesisComparison: "The audience captured the core message but missed duration and T-cell specifics.",
                claimCoverages:   new ResultClaimCoverage[]
                {
                    new("Sleep consolidates declarative memory.",  "yes",     "paragraph mentions memory consolidation"),
                    new("Sleep loss reduces T-cell counts.",       "partial", "immunity mentioned but not T-cells"),
                    new("Adults need 7–9 hours per night.",        "no",      null),
                },
                recall: 0.5) : null;

            return new DeepDiveResult(
                new ResultCriterion(4, "The central message came through clearly in most segments."),
                new ResultCriterion(3, "A recognisable intro and outro were present but the flow between them was uneven."),
                new ResultCriterion(4, "The narrative stayed on-topic with only minor tangents."),
                new ResultCriterion(3, "Claims were supported with examples but the evidence chain was sometimes loose."),
                new ResultCriterion(5, "Speech was fluent and grammatically clean throughout."),
                new ResultCriterion(0, "N/A — no expressive moments detected."),
                new ResultCriterion(0, "N/A — no Q&A rounds were recorded."),
                cmp);
        }
    }

    // ── DeepDive per-role detail records (Stage 2) ─────────────────────────
    //
    // Projected from the module's DeepDiveSubInputs at end-of-pipeline. Each
    // is optional on the bundle (null when the sub-role was skipped or input
    // was empty).

    public sealed class DeepDiveChunkRow
    {
        public int    Index     { get; }
        public string Retelling { get; }
        public string Label     { get; } // structure/consistency/support label

        public DeepDiveChunkRow(int index, string retelling, string label)
        {
            Index     = index;
            Retelling = retelling ?? string.Empty;
            Label     = label     ?? string.Empty;
        }
    }

    public sealed class DeepDiveSliceLine
    {
        public long   Sequence { get; }
        public string Text     { get; }

        public DeepDiveSliceLine(long sequence, string text)
        {
            Sequence = sequence;
            Text     = text ?? string.Empty;
        }
    }

    public sealed class DeepDiveDistributionBucket
    {
        public string Label { get; }
        public int    Count { get; }

        public DeepDiveDistributionBucket(string label, int count)
        {
            Label = label ?? string.Empty;
            Count = count;
        }
    }

    public sealed class DeepDiveDynamicsNote
    {
        public string Tag        { get; } // emotion or grammar value
        public string Transcript { get; }

        public DeepDiveDynamicsNote(string tag, string transcript)
        {
            Tag        = tag        ?? string.Empty;
            Transcript = transcript ?? string.Empty;
        }
    }

    // Used by Structure / ConsistencyFocus / SupportJustification — same shape.
    public sealed class DeepDiveScoreSliceDetails
    {
        public IReadOnlyList<DeepDiveChunkRow> Rows          { get; }
        public int                             ComputedValue { get; }
        public string                          ComputedLabel { get; }

        public DeepDiveScoreSliceDetails(
            IReadOnlyList<DeepDiveChunkRow> rows,
            int computedValue,
            string computedLabel)
        {
            Rows          = rows          ?? Array.Empty<DeepDiveChunkRow>();
            ComputedValue = computedValue;
            ComputedLabel = computedLabel ?? string.Empty;
        }
    }

    public sealed class DeepDiveLanguageDetails
    {
        public IReadOnlyList<DeepDiveSliceLine>          WeakSlices         { get; }
        public int                                       ComputedValue      { get; }
        public string                                    ComputedLabel      { get; }
        public string                                    DistributionOverall{ get; }
        public IReadOnlyList<DeepDiveDistributionBucket> Distribution       { get; }
        public IReadOnlyList<DeepDiveDynamicsNote>       Notes              { get; }

        public DeepDiveLanguageDetails(
            IReadOnlyList<DeepDiveSliceLine> weakSlices,
            int computedValue, string computedLabel,
            string distributionOverall,
            IReadOnlyList<DeepDiveDistributionBucket> distribution,
            IReadOnlyList<DeepDiveDynamicsNote> notes)
        {
            WeakSlices          = weakSlices          ?? Array.Empty<DeepDiveSliceLine>();
            ComputedValue       = computedValue;
            ComputedLabel       = computedLabel       ?? string.Empty;
            DistributionOverall = distributionOverall ?? string.Empty;
            Distribution        = distribution        ?? Array.Empty<DeepDiveDistributionBucket>();
            Notes               = notes               ?? Array.Empty<DeepDiveDynamicsNote>();
        }
    }

    // One Q&A round in the Results panel — phase = Live | Final,
    // resolution = resolved | not answered (decided programmatically inside
    // the session module from the inquirer reflections joined by concern id).
    public sealed class DeepDiveQaSpan
    {
        public string Phase      { get; } // "Live" | "Final"
        public string Question   { get; }
        public string Answer     { get; }
        public string Resolution { get; } // "resolved" | "not answered"

        public DeepDiveQaSpan(string phase, string question, string answer, string resolution)
        {
            Phase      = phase      ?? string.Empty;
            Question   = question   ?? string.Empty;
            Answer     = answer     ?? string.Empty;
            Resolution = resolution ?? string.Empty;
        }
    }

    public sealed class DeepDiveQaDetails
    {
        public IReadOnlyList<DeepDiveQaSpan> Spans { get; }

        public DeepDiveQaDetails(IReadOnlyList<DeepDiveQaSpan> spans)
        {
            Spans = spans ?? Array.Empty<DeepDiveQaSpan>();
        }
    }

    public sealed class DeepDiveEmotionDetails
    {
        public string                                    DistributionOverall { get; }
        public IReadOnlyList<DeepDiveDistributionBucket> Distribution        { get; }
        public IReadOnlyList<DeepDiveDynamicsNote>       Notes               { get; }

        public DeepDiveEmotionDetails(
            string distributionOverall,
            IReadOnlyList<DeepDiveDistributionBucket> distribution,
            IReadOnlyList<DeepDiveDynamicsNote> notes)
        {
            DistributionOverall = distributionOverall ?? string.Empty;
            Distribution        = distribution        ?? Array.Empty<DeepDiveDistributionBucket>();
            Notes               = notes               ?? Array.Empty<DeepDiveDynamicsNote>();
        }
    }

    public sealed class DeepDiveDetailsBundle
    {
        public DeepDiveScoreSliceDetails Structure            { get; }
        public DeepDiveScoreSliceDetails ConsistencyFocus     { get; }
        public DeepDiveScoreSliceDetails SupportJustification { get; }
        public DeepDiveLanguageDetails   LanguageQuality      { get; }
        public DeepDiveEmotionDetails    EmotionalDelivery    { get; }
        public DeepDiveQaDetails         QaHandling           { get; }

        public DeepDiveDetailsBundle(
            DeepDiveScoreSliceDetails structure,
            DeepDiveScoreSliceDetails consistencyFocus,
            DeepDiveScoreSliceDetails supportJustification,
            DeepDiveLanguageDetails   languageQuality,
            DeepDiveEmotionDetails    emotionalDelivery,
            DeepDiveQaDetails         qaHandling)
        {
            Structure            = structure;
            ConsistencyFocus     = consistencyFocus;
            SupportJustification = supportJustification;
            LanguageQuality      = languageQuality;
            EmotionalDelivery    = emotionalDelivery;
            QaHandling           = qaHandling;
        }
    }

    // Per-chunk row in the Results panel transcript table. Sequence is 1-based
    // (display index). Offset is relative to the first chunk (00:00). Emotion
    // and Grammar may be null when the Perceptor classified the chunk as messy
    // (no audible content) — render as "—" in the UI.
    public sealed class TranscriptChunkRecord
    {
        public int      Index   { get; }
        public TimeSpan Offset  { get; }
        public string   Text    { get; }
        public string   Emotion { get; }
        public string   Grammar { get; }

        public TranscriptChunkRecord(int index, TimeSpan offset, string text, string emotion, string grammar)
        {
            Index   = index;
            Offset  = offset;
            Text    = text    ?? string.Empty;
            Emotion = emotion;
            Grammar = grammar;
        }
    }

    // Full payload handed to the Results panel. Bundles the DeepDive scorecard
    // with the two short-text post-performance outputs (transcript-derived
    // inferred main idea + audience-call audience thesis) and the per-chunk
    // transcript table. Built on the worker thread inside
    // BeginEvaluation right after RunPostPerformancePipeline returns; fired via
    // SessionResultsAvailable.
    public sealed class SessionResultsPayload
    {
        public DeepDiveResult                          DeepDive                       { get; }
        public string                                  InferredMainIdeaFromTranscript { get; }
        public string                                  AudienceThesis                 { get; }
        public IReadOnlyList<TranscriptChunkRecord>    Transcript                     { get; }
        public DeepDiveDetailsBundle                   Details                        { get; }

        public SessionResultsPayload(
            DeepDiveResult                       deepDive,
            string                               inferredMainIdeaFromTranscript,
            string                               audienceThesis,
            IReadOnlyList<TranscriptChunkRecord> transcript,
            DeepDiveDetailsBundle                details = null)
        {
            DeepDive                       = deepDive;
            InferredMainIdeaFromTranscript = inferredMainIdeaFromTranscript;
            AudienceThesis                 = audienceThesis;
            Transcript                     = transcript ?? Array.Empty<TranscriptChunkRecord>();
            Details                        = details;
        }

        // Dev factory — wraps DeepDiveResult.ForDev with plausible auxiliary
        // text plus a short transcript so the ResultsPanelTestDriver can
        // smoke-test the full layout without the inference pipeline.
        public static SessionResultsPayload ForDev(DeepDiveResult deepDive, bool withTranscript = true)
        {
            TranscriptChunkRecord[] t = withTranscript
                ? new TranscriptChunkRecord[]
                  {
                      new(1, TimeSpan.Zero,                "Welcome everyone. Today I'd like to talk about why sleep matters more than most of us realise.", "calm",         "good"),
                      new(2, TimeSpan.FromSeconds(11),     "Recent studies show that sleep consolidates memory during the deep stages of the night.",        "calm",         "excellent"),
                      new(3, TimeSpan.FromSeconds(24),     "Um, you know, it also affects, like, your immune system, which is, uh, super important.",         "uncertain",    "poor"),
                      new(4, TimeSpan.FromSeconds(38),     "When you don't sleep enough, T-cell production drops noticeably within just a few days.",         "enthusiastic", "good"),
                      new(5, TimeSpan.FromSeconds(55),     "So the takeaway is: seven to nine hours, every night, no exceptions.",                            "enthusiastic", "excellent"),
                      new(6, TimeSpan.FromSeconds(68),     "Thank you for listening, and sleep well tonight.",                                                "calm",         "good"),
                  }
                : Array.Empty<TranscriptChunkRecord>();

            var chunkRows = new DeepDiveChunkRow[]
            {
                new(1, "introduces sleep importance",              "intro"),
                new(2, "explains memory consolidation",            "development"),
                new(3, "covers immune system effects (vague)",     "development"),
                new(4, "details T-cell drop",                      "development"),
                new(5, "actionable takeaway: 7-9 hours/night",     "conclusion"),
                new(6, "closing thanks",                           "conclusion"),
            };

            var grammarDist = new DeepDiveDistributionBucket[]
            {
                new("excellent", 2), new("good", 3), new("moderate", 0), new("poor", 1),
            };
            var grammarNotes = new DeepDiveDynamicsNote[]
            {
                new("poor", "Um, you know, it also affects, like, your immune system, which is, uh, super important."),
            };
            var weakSlices = new DeepDiveSliceLine[]
            {
                new(3, "Um, you know, it also affects, like, your immune system, which is, uh, super important."),
            };

            var emotionDist = new DeepDiveDistributionBucket[]
            {
                new("calm", 3), new("enthusiastic", 2), new("uncertain", 1), new("tense", 0),
            };
            var emotionNotes = new DeepDiveDynamicsNote[]
            {
                new("enthusiastic", "So the takeaway is: seven to nine hours, every night, no exceptions."),
                new("uncertain",    "Um, you know, it also affects, like, your immune system..."),
            };

            var qaSpans = new DeepDiveQaSpan[]
            {
                new("Live",  "How does sleep actually consolidate memory?",                     "During slow-wave sleep the hippocampus replays the day's events and transfers them to the cortex.",        "resolved"),
                new("Live",  "What's the difference between deep sleep and REM?",              "Deep sleep is restorative and physical; REM is when the brain processes emotion and forms long-term memory.", "resolved"),
                new("Live",  "Can you catch up on sleep over the weekend?",                    "Uh, well, kind of, but not really — the sleep debt isn't fully recoverable in one or two nights.",          "not answered"),
                new("Final", "Is 6 hours enough for some people?",                             "Most adults underperform on 6 — the seven-to-nine range is where the evidence lands.",                       "resolved"),
                new("Final", "Does coffee in the afternoon affect sleep that night?",          "Yes — caffeine's half-life is ~5 hours, so an afternoon coffee still has impact at bedtime.",                "resolved"),
            };

            var details = new DeepDiveDetailsBundle(
                structure:            new DeepDiveScoreSliceDetails(chunkRows, 4, "Strong"),
                consistencyFocus:     new DeepDiveScoreSliceDetails(chunkRows, 4, "Strong"),
                supportJustification: new DeepDiveScoreSliceDetails(chunkRows, 3, "Adequate"),
                languageQuality:      new DeepDiveLanguageDetails(
                                          weakSlices: weakSlices,
                                          computedValue: 4, computedLabel: "Strong",
                                          distributionOverall: "mostly good",
                                          distribution: grammarDist,
                                          notes: grammarNotes),
                emotionalDelivery:    new DeepDiveEmotionDetails(
                                          distributionOverall: "mixed, balanced",
                                          distribution: emotionDist,
                                          notes: emotionNotes),
                qaHandling:           new DeepDiveQaDetails(qaSpans));

            return new SessionResultsPayload(
                deepDive,
                "Sleep is a foundational health practice — it consolidates memory, supports immunity, and regulates metabolism, and seven to nine hours per night is the evidence-backed target.",
                "Sleep is vital for memory and immunity, and sleep deprivation harms both.",
                t,
                details);
        }
    }

    // ── Verbose logger ──────────────────────────────────────────────────────
    // Subscribes to every module event and prints the parsed payload to the
    // Unity Console. Mirrors the PoC's console output so we can verify the
    // session module's behaviour at a glance without opening JSONL logs.
    //
    // Each handler runs on the worker thread (module events fire there).
    // Debug.Log calls are dispatched through MainThreadDispatcher so console
    // entries stay ordered relative to other Unity logs and the editor's
    // stacktrace capture is happy.
    internal static class SessionVerboseLogger
    {
        const int MaxTranscriptChars   = 240;
        const int MaxRetellingChars    = 240;
        const int MaxMainIdeaChars     = 240;
        const int MaxExaminationChars  = 300;
        const int MaxClaimChars        = 240;
        const int MaxVerdictChars      = 240;
        const int MaxQuestionChars     = 240;

        static bool On => SessionLayer.VerboseLogging;

        static void Log(string msg)
        {
            if (!On) return;
            MainThreadDispatcher.Enqueue(() => UnityEngine.Debug.Log(msg));
        }

        static void LogWarn(string msg)
        {
            if (!On) return;
            MainThreadDispatcher.Enqueue(() => UnityEngine.Debug.LogWarning(msg));
        }

        static string Truncate(string s, int max)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            return s.Length <= max ? s : s.Substring(0, max) + "...";
        }

        public static void LogPerceptorTurn(GemmaStage.Session.Perceptor.PerceptorTurnResult turn)
        {
            if (!On) return;
            var p = turn.Parse;
            if (p.Audio is { } a)
            {
                Log($"[Perceptor.audio] clarity={a.Clarity} emotion={(a.Emotion?.ToString() ?? "-")} grammar={(a.Grammar?.ToString() ?? "-")} chunk_completed={a.ChunkCompleted} notes={a.Notes.Count}\n  transcript: \"{Truncate(a.Transcript, MaxTranscriptChars)}\"");
            }
            else if (p.Image is { } img)
            {
                Log($"[Perceptor.image] notes={img.Notes.Count}\n  examination: \"{Truncate(img.Examination, MaxExaminationChars)}\"");
            }
            else if (!string.IsNullOrEmpty(p.Error))
            {
                LogWarn($"[Perceptor] parse error: {p.Error}");
            }
        }

        public static void LogCycleStarting(int index)
        {
            Log($"[Cycle #{index}] starting");
        }

        public static void LogIdeaReflectorTurn(GemmaStage.Session.IdeaReflector.IdeaReflectorTurnResult turn)
        {
            if (!On) return;
            var r = turn.Parse.Reflection;
            if (r != null)
            {
                Log($"[IdeaReflector] topic=\"{r.Topic}\"\n  retelling: \"{Truncate(r.Retelling, MaxRetellingChars)}\"\n  main_idea_understanding: \"{Truncate(r.MainIdeaUnderstanding, MaxMainIdeaChars)}\"");
            }
            else if (!string.IsNullOrEmpty(turn.Parse.Error))
            {
                LogWarn($"[IdeaReflector] parse error: {turn.Parse.Error}");
            }
        }

        public static void LogInquirerTurn(GemmaStage.Session.Inquirer.InquirerTurnResult turn)
        {
            if (!On) return;
            var r = turn.Parse.Reflection;
            if (r != null)
            {
                var confusion = turn.State.ConfusionScore?.ToString() ?? "-";
                var added = r.NewConcerns.Count == 0
                    ? string.Empty
                    : "\n  +concerns: " + string.Join(", ", r.NewConcerns.Select(c => $"#{c.Id}({c.Type}):\"{Truncate(c.Question, 100)}\""));
                var removed = r.RemovedConcerns.Count == 0
                    ? string.Empty
                    : "\n  -concerns: " + string.Join(", ", r.RemovedConcerns.Select(rc => $"#{rc.Id}({rc.Cause})"));
                Log($"[Inquirer] confusion={confusion} new={r.NewConcerns.Count} removed={r.RemovedConcerns.Count} open_total={turn.State.Concerns.Count}{added}{removed}");
            }
            else if (!string.IsNullOrEmpty(turn.Parse.Error))
            {
                LogWarn($"[Inquirer] parse error: {turn.Parse.Error}");
            }
        }

        public static void LogCycleCompleted(GemmaStage.Session.CognitiveCycle.CognitiveCycleResult result)
        {
            if (!On) return;
            Log($"[Cycle #{result.Index}] done — queue drained {result.Queue.Drained} in {result.Queue.DrainTime.TotalMilliseconds:0}ms, open concerns={result.StateAfter.Concerns.Count}");
        }

        public static void LogSessionEnded()
        {
            Log("[Session] live phase ended.");
        }

        public static void LogTranscriptSummarizerMap(GemmaStage.Session.TranscriptSummarizer.TranscriptSummarizerMapStageResult result)
        {
            if (!On) return;
            Log($"[TranscriptSummarizer.MAP] {result.MapOutputs.Count} output(s)");
            for (int i = 0; i < result.MapOutputs.Count; i++)
            {
                var m = result.MapOutputs[i];
                Log($"  [{i + 1}] structure=\"{Truncate(m.Structure, 80)}\" | consistency=\"{Truncate(m.Consistency, 80)}\" | support=\"{Truncate(m.Support, 80)}\"\n      retelling: \"{Truncate(m.Retelling, 200)}\"");
            }
        }

        public static void LogTranscriptSummarizerReduce(GemmaStage.Session.TranscriptSummarizer.TranscriptSummarizerReduceStageResult result)
        {
            if (!On) return;
            if (result.ReduceOutput is { } r)
                Log($"[TranscriptSummarizer.REDUCE] inferred_main_idea: \"{Truncate(r.InferredMainIdeaFromTranscript, 400)}\"");
            else
                LogWarn("[TranscriptSummarizer.REDUCE] no output produced");
        }

        public static void LogGroundTruthSummarizer(GemmaStage.Session.GroundTruthSummarizer.GroundTruthSummarizerResult result)
        {
            if (!On) return;
            if (result.Ran && result.Output is { } o)
            {
                Log($"[GroundTruthSummarizer] source={(result.SourcePath ?? "<inline>")} kind={result.SourceKind}\n  main_thesis: \"{Truncate(o.MainThesis, 300)}\"\n  claims ({o.Claims.Count}):");
                for (int i = 0; i < o.Claims.Count; i++)
                    Log($"    - {Truncate(o.Claims[i], MaxClaimChars)}");
            }
            else
            {
                Log($"[GroundTruthSummarizer] skipped (ran={result.Ran}, source={result.SourcePath ?? "<none>"})");
            }
        }

        public static void LogMainIdeaComparator(GemmaStage.Session.MainIdeaComparator.MainIdeaComparatorResult result)
        {
            if (!On) return;
            if (result.Output is { } o)
            {
                Log($"[MainIdeaComparator] recall={o.Recall:0.00} anchor_claims={o.AnchorClaims.Count}\n  anchor_thesis:   \"{Truncate(o.AnchorThesis, 200)}\"\n  audience_thesis: \"{Truncate(o.AudienceThesis, 200)}\"\n  thesis_comparison: \"{Truncate(o.ThesisComparison, 300)}\"");
                for (int i = 0; i < o.ClaimCoverages.Count; i++)
                {
                    var c = o.ClaimCoverages[i];
                    Log($"    [{c.Coverage}] {Truncate(c.AnchorClaim, 180)}");
                }
            }
            else
            {
                Log($"[MainIdeaComparator] skipped: {result.SkipReason ?? "<unknown>"}");
            }
        }

        public static void LogClarification(GemmaStage.Session.Clarification.ClarificationResult result)
        {
            if (!On) return;
            if (!result.Ran) { Log("[Clarification] skipped"); return; }
            Log($"[Clarification] archived={result.OriginalArchivedCount} revised={result.RevisedQuestions.Count} resolved={result.ResolvedIds.Count} unresolved={result.UnresolvedQuestions.Count}");
            for (int i = 0; i < result.UnresolvedQuestions.Count; i++)
            {
                var q = result.UnresolvedQuestions[i];
                Log($"    [{q.Type}] #{q.Id}: \"{Truncate(q.Question, MaxQuestionChars)}\"");
            }
        }

        public static void LogDeepDiveSubRole(GemmaStage.Session.DeepDive.DeepDiveSubRole role, GemmaStage.Session.DeepDive.DeepDiveCriterion criterion)
        {
            if (!On) return;
            Log($"[DeepDive.{role}] {criterion.Value} — {Truncate(criterion.Verdict, MaxVerdictChars)}");
        }

        public static void LogDeepDive(GemmaStage.Session.DeepDive.DeepDiveResult r)
        {
            if (!On) return;
            Log("[DeepDive] full result:\n" +
                $"  MainIdeaClarity:      {r.MainIdeaClarity.Value} — {Truncate(r.MainIdeaClarity.Verdict, MaxVerdictChars)}\n" +
                $"  Structure:            {r.Structure.Value} — {Truncate(r.Structure.Verdict, MaxVerdictChars)}\n" +
                $"  ConsistencyFocus:     {r.ConsistencyFocus.Value} — {Truncate(r.ConsistencyFocus.Verdict, MaxVerdictChars)}\n" +
                $"  SupportJustification: {r.SupportJustification.Value} — {Truncate(r.SupportJustification.Verdict, MaxVerdictChars)}\n" +
                $"  LanguageQuality:      {r.LanguageQuality.Value} — {Truncate(r.LanguageQuality.Verdict, MaxVerdictChars)}\n" +
                $"  EmotionalDelivery:    {r.EmotionalDelivery.Value} — {Truncate(r.EmotionalDelivery.Verdict, MaxVerdictChars)}\n" +
                $"  QaHandling:           {r.QaHandling.Value} — {Truncate(r.QaHandling.Verdict, MaxVerdictChars)}");
        }
    }
}
