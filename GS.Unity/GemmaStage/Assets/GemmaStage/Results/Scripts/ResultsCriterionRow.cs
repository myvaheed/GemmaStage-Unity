using GemmaStage.Core;
using GemmaStage.Session;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Results
{
    // View component for one swimlane row in the Results panel. Carries a
    // colored score badge, category name, verdict text, and a collapsible
    // Details section. Wired by ResultsPanelBuilder; populated at runtime
    // by ResultsPanelView.
    [DisallowMultipleComponent]
    public sealed class ResultsCriterionRow : MonoBehaviour
    {
        [SerializeField] Image         _badgeImage;
        [SerializeField] TMP_Text      _scoreLabel;
        [SerializeField] TMP_Text      _nameLabel;
        [SerializeField] Button        _detailsToggle;
        [SerializeField] RectTransform _detailsChevron;
        [SerializeField] TMP_Text      _verdictLabel;
        [SerializeField] GameObject    _detailsPanel;
        [SerializeField] TMP_Text      _detailsLabel;

        bool _expanded;

        void Awake()
        {
            if (_detailsToggle != null)
                _detailsToggle.onClick.AddListener(ToggleDetails);
            if (_detailsPanel != null)
                _detailsPanel.SetActive(false);
        }

        void OnDestroy()
        {
            if (_detailsToggle != null)
                _detailsToggle.onClick.RemoveListener(ToggleDetails);
        }

        public void Populate(string categoryName, ResultCriterion criterion, string detailsText = null)
        {
            if (_nameLabel    != null) _nameLabel.text    = categoryName;
            if (_verdictLabel != null) _verdictLabel.text = criterion.Verdict;

            bool na = criterion.Value == 0;
            if (_scoreLabel != null) _scoreLabel.text = na ? "N/A" : criterion.Value.ToString();
            if (_badgeImage != null) _badgeImage.color = na ? Styles.SecondaryText : ScoreColor(criterion.Value);

            bool hasDetails = !string.IsNullOrWhiteSpace(detailsText);
            if (_detailsToggle != null)
                _detailsToggle.gameObject.SetActive(hasDetails);
            if (_detailsLabel != null)
                _detailsLabel.text = detailsText ?? string.Empty;

            // Reset collapsed state on repopulate.
            _expanded = false;
            if (_detailsPanel != null) _detailsPanel.SetActive(false);
            ApplyChevron();
        }

        void ToggleDetails()
        {
            _expanded = !_expanded;
            if (_detailsPanel != null) _detailsPanel.SetActive(_expanded);
            ApplyChevron();

            // Force the parent scroll-content layout to recalculate height so
            // the ScrollRect updates its scrollable range after the expand/collapse.
            LayoutRebuilder.ForceRebuildLayoutImmediate(transform.parent as RectTransform);
        }

        void ApplyChevron()
        {
            if (_detailsChevron == null) return;
            _detailsChevron.localEulerAngles = new Vector3(0f, 0f, _expanded ? 0f : -90f);
        }

        // Score → badge background color.
        static Color32 ScoreColor(int score) => score switch
        {
            5 => Styles.IconSuccess,   // green
            4 => Styles.Primary,       // purple
            3 => Styles.Warning,       // orange
            _ => Styles.Error,         // red  (1–2)
        };
    }
}
