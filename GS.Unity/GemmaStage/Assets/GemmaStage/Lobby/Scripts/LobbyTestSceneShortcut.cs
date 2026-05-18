using System.Reflection;
using System.Threading.Tasks;
using TMPro;
using Unity.XR.CoreUtils;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using GemmaStage.Core;

namespace GemmaStage.Lobby
{
    // Dev / QA shortcut. Drop this on any GameObject in the Lobby scene
    // (most natural choice: the SessionSetupPanel root). On Start it clones
    // the Start Session button, places the clone directly above it,
    // re-labels it, and wires it to load the target scene using the same
    // sequence EnvironmentManager.LoadEnvironment uses for Stage / Classroom:
    //
    //   1. LoadSceneAsync(name, Additive) — keeps Shared.unity (XR Rig) alive
    //   2. SetActiveScene(loaded scene)   — lighting / RenderSettings take
    //   3. UnloadSceneAsync(Lobby)        — drop the source UI
    //   4. Move the XR Rig to the test scene's SpawnPoint (if present)
    //
    // The target scene must:
    //   - be added to Build Settings
    //   - have its Main Camera's TargetDisplay set to 2 (so it does not fight
    //     the XR Rig camera on display 1), matching Stage.unity
    //   - optionally contain a SpawnPoint GameObject for XR Rig placement
    //
    // Remove this component to hide the button.
    [DisallowMultipleComponent]
    public sealed class LobbyTestSceneShortcut : MonoBehaviour
    {
        [Tooltip("Scene name (no extension) to load when the test button is pressed. Must be in Build Settings.")]
        [SerializeField] string sceneName = "ResultsPanelTest";

        [Tooltip("Label text shown on the test button.")]
        [SerializeField] string buttonLabel = "Open Test Scene";

        [Tooltip("Optional override. If null, the Start Session button is auto-located via SessionSetupPanel reflection at Start.")]
        [SerializeField] Button anchorButton;

        GameObject _spawned;

        void Start()
        {
            // Test-scene shortcut button is currently DISABLED. Uncomment the
            // block below to surface the "Open Test Scene" button next to
            // Start Session in the Lobby.
            // if (anchorButton == null)
            //     anchorButton = FindStartSessionButton();
            //
            // if (anchorButton == null)
            // {
            //     Debug.LogWarning("[LobbyTestSceneShortcut] Start Session button not found — shortcut not added.", this);
            //     return;
            // }
            //
            // _spawned = SpawnTestButton(anchorButton);
        }

        void OnDestroy()
        {
            if (_spawned != null) Destroy(_spawned);
        }

        static Button FindStartSessionButton()
        {
            var panel = FindAnyObjectByType<SessionSetupPanel>();
            if (panel == null) return null;
            var field = typeof(SessionSetupPanel).GetField(
                "startSessionButton",
                BindingFlags.NonPublic | BindingFlags.Instance);
            return field?.GetValue(panel) as Button;
        }

        GameObject SpawnTestButton(Button anchor)
        {
            var parent = anchor.transform.parent;
            var clone  = Instantiate(anchor.gameObject, parent);
            clone.name = "TestSceneShortcutButton";

            int anchorIdx = anchor.transform.GetSiblingIndex();
            clone.transform.SetSiblingIndex(Mathf.Max(0, anchorIdx));

            var label = clone.GetComponentInChildren<TMP_Text>(includeInactive: true);
            if (label != null) label.text = buttonLabel;

            var btn = clone.GetComponent<Button>();
            if (btn != null)
            {
                btn.interactable = true;
                btn.onClick.RemoveAllListeners();
                btn.onClick.AddListener(OnTestButtonClicked);
            }

            return clone;
        }

        async void OnTestButtonClicked()
        {
            if (string.IsNullOrWhiteSpace(sceneName))
            {
                Debug.LogWarning("[LobbyTestSceneShortcut] sceneName is empty.", this);
                return;
            }
            try
            {
                await LoadTestSceneLikeEnvironmentAsync(sceneName);
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[LobbyTestSceneShortcut] Load failed: {ex}", this);
            }
        }

        // Mirrors EnvironmentManager.LoadEnvironment so the XR Rig in
        // Shared.unity drives the headset throughout: additive load → set
        // active → unload Lobby → reposition XR Rig at the test scene's
        // SpawnPoint (when present).
        static async Task LoadTestSceneLikeEnvironmentAsync(string sceneName)
        {
            var op = SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
            if (op == null)
                throw new System.InvalidOperationException(
                    $"Scene '{sceneName}' not found in Build Settings.");
            await AsTask(op);

            var loaded = SceneManager.GetSceneByName(sceneName);
            if (loaded.IsValid()) SceneManager.SetActiveScene(loaded);

            await UnloadIfLoadedAsync("Lobby");

            MoveXrRigToSpawnPoint(loaded);
        }

        static async Task UnloadIfLoadedAsync(string name)
        {
            var s = SceneManager.GetSceneByName(name);
            if (!s.IsValid() || !s.isLoaded) return;
            var op = SceneManager.UnloadSceneAsync(s);
            if (op != null) await AsTask(op);
        }

        static void MoveXrRigToSpawnPoint(Scene scene)
        {
            SpawnPoint spawn = null;
            foreach (var root in scene.GetRootGameObjects())
            {
                spawn = root.GetComponentInChildren<SpawnPoint>(true);
                if (spawn != null) break;
            }
            if (spawn == null) return;

            var origin = FindAnyObjectByType<XROrigin>();
            if (origin == null) return;

            origin.transform.SetPositionAndRotation(spawn.transform.position, spawn.transform.rotation);
        }

        static Task AsTask(AsyncOperation op)
        {
            var tcs = new TaskCompletionSource<bool>();
            op.completed += _ => tcs.TrySetResult(true);
            return tcs.Task;
        }
    }
}
