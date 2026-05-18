using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Results
{
    // Collapsible section block used inside the Results panel scroll content.
    // The header row (the whole RectTransform behind the title) is itself the
    // toggle button — clicking anywhere on the row flips _expanded. The
    // chevron icon is the built-in DropdownArrow sprite, rotated -90° when
    // collapsed (points right) and 0° when expanded (points down) — matches
    // the dropdown widget in the Lobby setting rows.
    //
    // Wired by ResultsPanelBuilder.
    [DisallowMultipleComponent]
    public sealed class ResultsExpandableSection : MonoBehaviour
    {
        [SerializeField] Button        _toggle;
        [SerializeField] TMP_Text      _titleLabel;
        [SerializeField] RectTransform _chevronIcon;
        [SerializeField] GameObject    _body;
        [SerializeField] RectTransform _bodyContent;

        bool _expanded;

        public RectTransform BodyContent => _bodyContent;

        void Awake()
        {
            if (_toggle != null) _toggle.onClick.AddListener(Toggle);
            ApplyExpanded(false);
        }

        void OnDestroy()
        {
            if (_toggle != null) _toggle.onClick.RemoveListener(Toggle);
        }

        public void Init(string title)
        {
            if (_titleLabel != null) _titleLabel.text = title ?? "";
        }

        void Toggle() => ApplyExpanded(!_expanded);

        void ApplyExpanded(bool expanded)
        {
            _expanded = expanded;
            if (_body != null) _body.SetActive(expanded);
            if (_chevronIcon != null)
                _chevronIcon.localEulerAngles = new Vector3(0f, 0f, expanded ? 0f : -90f);
        }
    }
}
