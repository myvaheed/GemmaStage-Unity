using System;
using System.IO;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using GemmaStage.Core;

namespace GemmaStage.Lobby
{
    public class SessionSetupPanel : MonoBehaviour
    {
        const int DurationMin = 4;
        const int DurationMax = 30;
        const int DurationStep = 2;
        const string GroundTruthEmptyLabel = "No document";
        const int GroundTruthRasterTargetWidthPx = 512;

        static readonly FilePickerFilter[] GroundTruthFilters =
        {
            new("Documents", "txt", "md", "pdf", "png", "jpg", "jpeg"),
            new("Text",      "txt", "md"),
            new("PDF",       "pdf"),
            new("Image",     "png", "jpg", "jpeg"),
        };

        [Header("Layout")]
        [SerializeField] TextMeshProUGUI titleLabel;
        [SerializeField] RectTransform contentRoot;

        [Header("Duration")]
        [SerializeField] Button durationMinusButton;
        [SerializeField] Button durationPlusButton;
        [SerializeField] TextMeshProUGUI durationValueLabel;

        [Header("Live Q&A")]
        [SerializeField] Toggle liveQaToggle;
        [SerializeField] TMP_Dropdown disturbanceDropdown;

        [Header("Final Q&A")]
        [SerializeField] Toggle finalQaToggle;

        [Header("Ground Truth Doc")]
        [SerializeField] Button groundTruthAttachButton;
        [SerializeField] Button groundTruthClearButton;
        [SerializeField] TextMeshProUGUI groundTruthFilenameLabel;

        [Header("Presentation")]
        [SerializeField] Button presentationAttachButton;
        [SerializeField] TextMeshProUGUI presentationCountLabel;
        [SerializeField] PresentationPickerPopup presentationPickerPopup;

        [Header("Start Session")]
        [SerializeField] Button startSessionButton;
        [Tooltip("Permanent hint shown below Start Session while the mic-verification gate has not tripped.")]
        [SerializeField] TextMeshProUGUI micHintLabel;
        [SerializeField, TextArea(2, 4)] string hintNoDevice =
            "No microphone detected. Connect one and pick it in Game Settings.";
        [SerializeField, TextArea(2, 4)] string hintListening =
            "Pick your microphone in Game Settings, then say something out loud — the volume meter must register your voice.";

        [Header("Mic Gate Wiring")]
        [Tooltip("Optional. Falls back to FindAnyObjectByType when null.")]
        [SerializeField] GameSettingsPanel gameSettingsPanel;

        static readonly IPdfPageEnumerator PdfEnumerator = new PdfToImagePdfPageEnumerator();

        public LobbySessionConfig Config { get; } = new LobbySessionConfig();

        public event Action<LobbySessionConfig> StartSessionRequested;

        enum MicGateState { NoDevice, Listening, Verified }
        MicGateState _micGateState = MicGateState.NoDevice;
        MicVolumeMonitor _subscribedMonitor;

        void Awake()
        {
            BindDurationControls();
            BindLiveQaControls();
            BindFinalQaControls();
            BindGroundTruthControls();
            BindPresentationControls();
            BindStartSessionButton();
        }

        void Start()
        {
            // Wire mic-verification gate after Awake so GameSettingsPanel.Awake
            // has populated its dropdown and the MicVolumeMonitor reference.
            // The gate starts in NoDevice/Listening; only a sustained loud
            // sample on the chosen mic flips it to Verified.
            WireMicGate();
        }

        void OnDestroy()
        {
            UnwireMicGate();
        }

        void BindDurationControls()
        {
            Config.DurationMinutes = Mathf.Clamp(Config.DurationMinutes, DurationMin, DurationMax);

            if (durationMinusButton != null)
                durationMinusButton.onClick.AddListener(() => StepDuration(-DurationStep));

            if (durationPlusButton != null)
                durationPlusButton.onClick.AddListener(() => StepDuration(DurationStep));

            RefreshDurationDisplay();
        }

        void StepDuration(int delta)
        {
            int next = Mathf.Clamp(Config.DurationMinutes + delta, DurationMin, DurationMax);
            if (next == Config.DurationMinutes)
                return;
            Config.DurationMinutes = next;
            RefreshDurationDisplay();
        }

        void RefreshDurationDisplay()
        {
            if (durationValueLabel != null)
                durationValueLabel.text = $"{Config.DurationMinutes} min";

            if (durationMinusButton != null)
                durationMinusButton.interactable = Config.DurationMinutes > DurationMin;

            if (durationPlusButton != null)
                durationPlusButton.interactable = Config.DurationMinutes < DurationMax;
        }

        void BindLiveQaControls()
        {
            if (disturbanceDropdown != null)
            {
                disturbanceDropdown.ClearOptions();
                disturbanceDropdown.AddOptions(new System.Collections.Generic.List<string>
                {
                    DisturbanceLevel.Low.ToString(),
                    DisturbanceLevel.Middle.ToString(),
                    DisturbanceLevel.High.ToString(),
                });
                disturbanceDropdown.SetValueWithoutNotify((int)Config.Disturbance);
                disturbanceDropdown.RefreshShownValue();
                disturbanceDropdown.onValueChanged.AddListener(OnDisturbanceChanged);
            }

            if (liveQaToggle != null)
            {
                liveQaToggle.SetIsOnWithoutNotify(Config.LiveQaEnabled);
                liveQaToggle.onValueChanged.AddListener(OnLiveQaToggled);
            }

            RefreshDisturbanceInteractable();
        }

        void OnLiveQaToggled(bool isOn)
        {
            Config.LiveQaEnabled = isOn;
            RefreshDisturbanceInteractable();
        }

        void OnDisturbanceChanged(int index)
        {
            Config.Disturbance = (DisturbanceLevel)index;
        }

        void RefreshDisturbanceInteractable()
        {
            if (disturbanceDropdown == null)
                return;

            disturbanceDropdown.interactable = Config.LiveQaEnabled;

            // CanvasGroup alpha drops with interactable so the caption text + arrow look obviously disabled,
            // not just the background tint that Selectable applies.
            var cg = disturbanceDropdown.GetComponent<CanvasGroup>();
            if (cg != null)
                cg.alpha = Config.LiveQaEnabled ? 1f : 0.45f;
        }

        void BindFinalQaControls()
        {
            if (finalQaToggle == null)
                return;

            finalQaToggle.SetIsOnWithoutNotify(Config.FinalQaEnabled);
            finalQaToggle.onValueChanged.AddListener(OnFinalQaToggled);
        }

        void OnFinalQaToggled(bool isOn)
        {
            Config.FinalQaEnabled = isOn;
        }

        void BindGroundTruthControls()
        {
            if (groundTruthAttachButton != null)
                groundTruthAttachButton.onClick.AddListener(OnGroundTruthAttachClicked);

            if (groundTruthClearButton != null)
                groundTruthClearButton.onClick.AddListener(OnGroundTruthClearClicked);

            RefreshGroundTruthDisplay();
        }

        void OnGroundTruthAttachClicked()
        {
            FilePicker.OpenFile("Select Ground Truth Document", GroundTruthFilters, path =>
            {
                if (string.IsNullOrEmpty(path))
                    return;

                if (TryAttachGroundTruth(path))
                    RefreshGroundTruthDisplay();
            });
        }

        void OnGroundTruthClearClicked()
        {
            ClearGroundTruthAttachment();
            RefreshGroundTruthDisplay();
        }

        bool TryAttachGroundTruth(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".txt":
                case ".md":
                    return TryAttachGroundTruthText(path);
                case ".pdf":
                    return TryAttachGroundTruthPdf(path);
                case ".png":
                case ".jpg":
                case ".jpeg":
                    return TryAttachGroundTruthImage(path);
                default:
                    Debug.LogWarning($"[SessionSetupPanel] Unsupported ground-truth document '{path}'.");
                    return false;
            }
        }

        bool TryAttachGroundTruthText(string path)
        {
            try
            {
                _ = File.ReadAllBytes(path);
                SetGroundTruthAttachment(path, GroundTruthAttachmentKind.Text, null, 0);
                return true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionSetupPanel] Failed to load ground-truth text '{path}': {ex.Message}", this);
                return false;
            }
        }

        bool TryAttachGroundTruthPdf(string path)
        {
            var pages = PdfEnumerator.RasterizeAllPages(path, GroundTruthRasterTargetWidthPx);
            if (pages == null || pages.Count == 0 || pages[0] == null)
            {
                Debug.LogWarning($"[SessionSetupPanel] Ground-truth PDF '{path}' produced no rasterizable first page.", this);
                return false;
            }

            var firstPage = pages[0];
            for (int i = 1; i < pages.Count; i++)
            {
                if (pages[i] != null)
                    Destroy(pages[i]);
            }

            SetGroundTruthAttachment(path, GroundTruthAttachmentKind.Pdf, firstPage, pages.Count);
            return true;
        }

        bool TryAttachGroundTruthImage(string path)
        {
            var tex = LoadGroundTruthImageTexture(path);
            if (tex == null)
                return false;

            SetGroundTruthAttachment(path, GroundTruthAttachmentKind.Image, tex, 0);
            return true;
        }

        static Texture2D LoadGroundTruthImageTexture(string path)
        {
            try
            {
                byte[] bytes = File.ReadAllBytes(path);
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                if (!tex.LoadImage(bytes, markNonReadable: true))
                {
                    Destroy(tex);
                    Debug.LogWarning($"[SessionSetupPanel] Ground-truth image '{path}' is not decodable.");
                    return null;
                }

                tex.name = Path.GetFileName(path);
                return tex;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[SessionSetupPanel] Failed to load ground-truth image '{path}': {ex.Message}");
                return null;
            }
        }

        void SetGroundTruthAttachment(
            string path,
            GroundTruthAttachmentKind kind,
            Texture2D renderedTexture,
            int pdfPageCount)
        {
            ClearGroundTruthAttachment();
            Config.GroundTruthDocPath = path;
            Config.GroundTruthKind = kind;
            Config.GroundTruthRenderedTexture = renderedTexture;
            Config.GroundTruthPdfPageCount = pdfPageCount;
        }

        void ClearGroundTruthAttachment()
        {
            if (Config.GroundTruthRenderedTexture != null)
                Destroy(Config.GroundTruthRenderedTexture);

            Config.GroundTruthDocPath = string.Empty;
            Config.GroundTruthKind = GroundTruthAttachmentKind.None;
            Config.GroundTruthRenderedTexture = null;
            Config.GroundTruthPdfPageCount = 0;
        }

        void RefreshGroundTruthDisplay()
        {
            bool hasPath = !string.IsNullOrEmpty(Config.GroundTruthDocPath);

            if (groundTruthFilenameLabel != null)
                groundTruthFilenameLabel.text = hasPath
                    ? Path.GetFileName(Config.GroundTruthDocPath)
                    : GroundTruthEmptyLabel;

            if (groundTruthClearButton != null)
                groundTruthClearButton.gameObject.SetActive(hasPath);
        }

        void BindPresentationControls()
        {
            if (presentationAttachButton != null)
                presentationAttachButton.onClick.AddListener(OnPresentationAttachClicked);

            if (presentationPickerPopup != null)
                presentationPickerPopup.gameObject.SetActive(false);

            RefreshPresentationDisplay();
        }

        void OnPresentationAttachClicked()
        {
            if (presentationPickerPopup == null)
                return;

            presentationPickerPopup.Open(Config.Slides, PdfEnumerator, RefreshPresentationDisplay);
        }

        void RefreshPresentationDisplay()
        {
            if (presentationCountLabel != null)
                presentationCountLabel.text = $"{Config.Slides.Count} slides attached";
        }


        void BindStartSessionButton()
        {
            if (startSessionButton == null) return;
            startSessionButton.onClick.AddListener(OnStartSessionClicked);
            RefreshStartSessionInteractable();
        }

        void RefreshStartSessionInteractable()
        {
            if (startSessionButton == null) return;
            startSessionButton.interactable =
                IsEnvironmentSet(Config.Environment) &&
                _micGateState == MicGateState.Verified;
        }

        void WireMicGate()
        {
            if (gameSettingsPanel == null)
                gameSettingsPanel = FindAnyObjectByType<GameSettingsPanel>(FindObjectsInactive.Include);

            if (gameSettingsPanel != null)
                gameSettingsPanel.MicrophoneDeviceChanged += OnMicrophoneDeviceChanged;

            SubscribeToMonitor();
            RefreshMicHintAndGate();
        }

        void UnwireMicGate()
        {
            if (gameSettingsPanel != null)
                gameSettingsPanel.MicrophoneDeviceChanged -= OnMicrophoneDeviceChanged;

            if (_subscribedMonitor != null)
            {
                _subscribedMonitor.MicSignalDetected -= OnMicSignalDetected;
                _subscribedMonitor = null;
            }
        }

        void SubscribeToMonitor()
        {
            var monitor = gameSettingsPanel != null ? gameSettingsPanel.MicrophoneVolumeMonitor : null;
            if (monitor == _subscribedMonitor) return;

            if (_subscribedMonitor != null)
                _subscribedMonitor.MicSignalDetected -= OnMicSignalDetected;

            _subscribedMonitor = monitor;
            if (_subscribedMonitor != null)
                _subscribedMonitor.MicSignalDetected += OnMicSignalDetected;
        }

        void OnMicrophoneDeviceChanged(string deviceName)
        {
            // Re-subscribe in case the panel swapped monitors (rare; defensive).
            SubscribeToMonitor();
            RefreshMicHintAndGate();
        }

        void OnMicSignalDetected()
        {
            _micGateState = MicGateState.Verified;
            ApplyMicHintVisibility();
            RefreshStartSessionInteractable();
        }

        void RefreshMicHintAndGate()
        {
            bool hasDevice = Microphone.devices != null && Microphone.devices.Length > 0;
            bool verified = _subscribedMonitor != null && _subscribedMonitor.IsVerifiedForCurrentDevice;

            if (!hasDevice)
                _micGateState = MicGateState.NoDevice;
            else if (verified)
                _micGateState = MicGateState.Verified;
            else
                _micGateState = MicGateState.Listening;

            ApplyMicHintVisibility();
            RefreshStartSessionInteractable();
        }

        void ApplyMicHintVisibility()
        {
            if (micHintLabel == null) return;

            switch (_micGateState)
            {
                case MicGateState.NoDevice:
                    micHintLabel.text = hintNoDevice;
                    micHintLabel.gameObject.SetActive(true);
                    break;
                case MicGateState.Listening:
                    micHintLabel.text = hintListening;
                    micHintLabel.gameObject.SetActive(true);
                    break;
                case MicGateState.Verified:
                    micHintLabel.gameObject.SetActive(false);
                    break;
            }
        }

        // Defensive — Stage is the default and the dropdown never clears, so this
        // is always true today. Kept as a guard in case future code paths add an
        // "unset" sentinel to EnvironmentId.
        static bool IsEnvironmentSet(EnvironmentId env) =>
            env == EnvironmentId.Stage || env == EnvironmentId.Classroom;

        void OnStartSessionClicked()
        {
            var listener = StartSessionRequested;
            if (listener != null)
            {
                listener(Config);
                return;
            }

            // Phase 5.4 introduces SessionManager which subscribes to the event
            // above. Until then fall back to a direct env load so the button is
            // functional during scene smoke-tests.
            var gm = GameManager.Instance;
            if (gm != null)
                _ = gm.LoadEnvironment(Config.Environment);
            else
                Debug.LogWarning("[SessionSetupPanel] Start Session clicked but no listener and no GameManager.", this);
        }
    }
}
