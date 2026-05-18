using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Lobby
{
    /// <summary>
    /// Toggles a "checked visuals" GameObject (purple fill + white check) in sync with
    /// a Unity Toggle. Unity's built-in Toggle.graphic only fades a single Graphic's
    /// alpha and doesn't propagate to child renderers, so we drive a sibling object's
    /// active state explicitly to compose multi-layer checked visuals.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(Toggle))]
    public sealed class CheckboxBinder : MonoBehaviour
    {
        [SerializeField] private GameObject checkedVisuals;

        private Toggle toggle;

        private void Awake()
        {
            toggle = GetComponent<Toggle>();
            toggle.onValueChanged.AddListener(OnChanged);
            // Intentionally NOT calling OnChanged here: GameSettingsPanel.Awake
            // runs SetIsOnWithoutNotify to load persisted state AFTER this Awake
            // (same frame, execution-order undefined across GOs). Deferring to
            // Start guarantees the toggle's isOn reflects the loaded setting.
        }

        private void Start()
        {
            OnChanged(toggle.isOn);
        }

        private void OnDestroy()
        {
            if (toggle != null) toggle.onValueChanged.RemoveListener(OnChanged);
        }

        private void OnChanged(bool isOn)
        {
            if (checkedVisuals != null) checkedVisuals.SetActive(isOn);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (checkedVisuals != null && Application.isPlaying == false)
            {
                var t = GetComponent<Toggle>();
                if (t != null) checkedVisuals.SetActive(t.isOn);
            }
        }
#endif
    }
}
