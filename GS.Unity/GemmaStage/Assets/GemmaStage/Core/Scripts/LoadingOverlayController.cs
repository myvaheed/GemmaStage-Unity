using UnityEngine;

namespace GemmaStage.Core
{
    // Singleton-style host for the blackout loading overlay shown during the
    // Setup → Stage transition (GS-222). Lives in Shared.unity so it survives
    // every Lobby↔Env scene swap. Lazy-instantiates the prefab on first Show().
    //
    // The overlay is parented to the active camera so it follows head motion
    // and fully occludes whatever the env scene is doing behind it (NPCs
    // popping in one by one, etc.). Distance is short — we want the canvas to
    // fill the FOV completely on both eyes.
    [DisallowMultipleComponent]
    public sealed class LoadingOverlayController : MonoBehaviour
    {
        public static LoadingOverlayController Instance { get; private set; }

        // Close enough to be in front of the near clip plane on most rigs and
        // wide enough at the prefab's scale to fully fill the headset FOV.
        const float OverlayDistanceMeters = 0.4f;

        [SerializeField] LoadingOverlayPanelView panelPrefab;

        LoadingOverlayPanelView _panel;
        bool _visible;

        void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            if (panelPrefab == null)
                Debug.LogError("[LoadingOverlayController] panelPrefab not assigned.");
        }

        void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        void LateUpdate()
        {
            // Track the camera every frame while visible — Camera.main may flip
            // between scene loads (the env scene's camera may briefly take over
            // before the XR rig's center-eye camera resolves).
            if (!_visible || _panel == null) return;
            PositionInFrontOfCamera();
        }

        public void Show()
        {
            if (_visible) return;
            EnsurePanel();
            if (_panel == null) return;

            PositionInFrontOfCamera();
            _panel.Show();
            _visible = true;
        }

        public void Hide()
        {
            if (!_visible) return;
            _visible = false;
            if (_panel != null) _panel.Hide();
        }

        void EnsurePanel()
        {
            if (_panel != null) return;
            if (panelPrefab == null) return;
            // Parent to this controller (which lives in Shared.unity) so the
            // panel survives the Lobby↔Env scene swap. Without this, Instantiate
            // puts the panel in the *active* scene (the Lobby at Show time),
            // and LoadSceneAsync's lobby-unload destroys it ~1s later — the
            // overlay vanishes long before NPC spawning is done.
            _panel = Instantiate(panelPrefab, transform);
            _panel.gameObject.SetActive(false);
        }

        void PositionInFrontOfCamera()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var t = cam.transform;
            _panel.transform.SetPositionAndRotation(
                t.position + t.forward * OverlayDistanceMeters,
                t.rotation);
        }
    }
}
