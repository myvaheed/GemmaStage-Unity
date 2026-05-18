using System;
using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Comfort;
using UnityEngine.XR.Interaction.Toolkit.Locomotion.Turning;
using System.Collections.Generic;
using GemmaStage.Core;

namespace GemmaStage.Lobby
{
    public class GameSettingsPanel : MonoBehaviour
    {
        [Header("Layout")]
        [SerializeField] TextMeshProUGUI titleLabel;
        [SerializeField] RectTransform contentRoot;

        [Header("Comfort Vignette")]
        [SerializeField] Toggle vignetteToggle;

        [Header("Turning Mode")]
        [SerializeField] TMP_Dropdown turningModeDropdown;

        [Header("Microphone")]
        [SerializeField] TMP_Dropdown microphoneDropdown;
        [SerializeField] MicVolumeMonitor microphoneVolumeMonitor;

        [Header("Language")]
        [SerializeField] TMP_Dropdown languageDropdown;

        [Header("Master Audio Volume")]
        [SerializeField] Slider masterVolumeSlider;
        // Slider runs in wholeNumbers mode 0..100 so the handle snaps to 1%
        // steps natively — same fix as the Echo slider in SessionSetupPanel.
        const int VolumeSliderSteps = 100;

        [Header("Watch Hand")]
        [SerializeField] TMP_Dropdown watchHandDropdown;

        [Header("Echo")]
        [SerializeField] Toggle echoToggle;
        [SerializeField] Slider echoLevelSlider;
        // Lives in Shared.unity on MicPlayback. Used so toggle/slider tweaks
        // preview immediately in the Lobby and any open session. Falls back to
        // FindAnyObjectByType when not Inspector-wired.
        [SerializeField] EchoProcessor echoProcessor;
        const int EchoSliderSteps = 100;

        const string NoMicrophoneOption = "(no microphone detected)";

        static readonly string[] LanguageDisplayNames =
        {
            "English", "French", "German", "Spanish", "Turkish", "Italian"
        };

        public GameSettings Settings { get; private set; } = new GameSettings();

        /// Exposed so SessionSetupPanel can subscribe to MicSignalDetected for
        /// the Start Session voice-verification gate.
        public MicVolumeMonitor MicrophoneVolumeMonitor => microphoneVolumeMonitor;

        /// Fires after `BindMicrophoneDropdown` finishes and on every
        /// dropdown change. Carries the active device name (empty when no
        /// device is available).
        public event Action<string> MicrophoneDeviceChanged;

        void Awake()
        {
            Settings = GameSettingsRepository.Load();
            BindVignetteToggle();
            BindTurningModeDropdown();
            BindMicrophoneDropdown();
            BindLanguageDropdown();
            BindMasterVolumeSlider();
            BindWatchHandDropdown();
            BindEchoControls();
        }

        void Start()
        {
            // Apply once on Start in case Shared is already loaded before this
            // Lobby scene finishes waking up. OnSceneLoaded covers the other
            // additive-ordering direction.
            ApplyVignette();
            ApplyTurningMode();
            ApplyMasterVolume();
            ApplyWatchHand();
            ApplyEcho();
        }

        void OnEnable()
        {
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void OnDisable()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        void BindVignetteToggle()
        {
            if (vignetteToggle == null)
                return;

            vignetteToggle.SetIsOnWithoutNotify(Settings.VignetteEnabled);
            vignetteToggle.onValueChanged.AddListener(OnVignetteToggled);
        }

        void OnVignetteToggled(bool isOn)
        {
            Settings.VignetteEnabled = isOn;
            ApplyVignette();
            GameSettingsRepository.Save(Settings);
        }

        void BindTurningModeDropdown()
        {
            if (turningModeDropdown == null)
                return;

            turningModeDropdown.ClearOptions();
            turningModeDropdown.AddOptions(new List<string>
            {
                TurningMode.Off.ToString(),
                TurningMode.Snap.ToString(),
            });
            turningModeDropdown.SetValueWithoutNotify((int)Settings.TurningMode);
            turningModeDropdown.RefreshShownValue();
            turningModeDropdown.onValueChanged.AddListener(OnTurningModeChanged);
        }

        void OnTurningModeChanged(int index)
        {
            Settings.TurningMode = (TurningMode)index;
            ApplyTurningMode();
            GameSettingsRepository.Save(Settings);
        }

        void BindMicrophoneDropdown()
        {
            if (microphoneDropdown == null)
                return;

            var devices = Microphone.devices;
            microphoneDropdown.ClearOptions();

            if (devices.Length == 0)
            {
                microphoneDropdown.AddOptions(new List<string> { NoMicrophoneOption });
                microphoneDropdown.interactable = false;
                Settings.MicrophoneDeviceName = string.Empty;
                microphoneDropdown.SetValueWithoutNotify(0);
                microphoneDropdown.RefreshShownValue();
                MicrophoneDeviceChanged?.Invoke(string.Empty);
                return;
            }

            microphoneDropdown.AddOptions(new List<string>(devices));
            microphoneDropdown.interactable = true;

            int selected = 0;
            for (int i = 0; i < devices.Length; i++)
            {
                if (devices[i] == Settings.MicrophoneDeviceName)
                {
                    selected = i;
                    break;
                }
            }

            Settings.MicrophoneDeviceName = devices[selected];
            microphoneDropdown.SetValueWithoutNotify(selected);
            microphoneDropdown.RefreshShownValue();
            microphoneDropdown.onValueChanged.AddListener(OnMicrophoneChanged);

            if (microphoneVolumeMonitor != null)
                microphoneVolumeMonitor.SetDevice(Settings.MicrophoneDeviceName);
            MicrophoneDeviceChanged?.Invoke(Settings.MicrophoneDeviceName);
        }

        void OnMicrophoneChanged(int index)
        {
            var devices = Microphone.devices;
            if (index < 0 || index >= devices.Length)
                return;

            Settings.MicrophoneDeviceName = devices[index];
            if (microphoneVolumeMonitor != null)
                microphoneVolumeMonitor.SetDevice(Settings.MicrophoneDeviceName);
            GameSettingsRepository.Save(Settings);
            MicrophoneDeviceChanged?.Invoke(Settings.MicrophoneDeviceName);
        }

        void BindLanguageDropdown()
        {
            if (languageDropdown == null)
                return;

            languageDropdown.ClearOptions();
            languageDropdown.AddOptions(new List<string>(LanguageDisplayNames));
            int idx = (int)Settings.Language;
            if (idx >= LanguageDisplayNames.Length) idx = 0;
            languageDropdown.SetValueWithoutNotify(idx);
            languageDropdown.RefreshShownValue();
            languageDropdown.onValueChanged.AddListener(OnLanguageChanged);
        }

        void OnLanguageChanged(int index)
        {
            Settings.Language = (LanguageCode)index;
            GameSettingsRepository.Save(Settings);
        }

        void BindMasterVolumeSlider()
        {
            if (masterVolumeSlider == null)
                return;

            masterVolumeSlider.wholeNumbers = true;
            masterVolumeSlider.minValue = 0f;
            masterVolumeSlider.maxValue = VolumeSliderSteps;
            masterVolumeSlider.SetValueWithoutNotify(
                Mathf.Round(Mathf.Clamp01(Settings.MasterVolume) * VolumeSliderSteps));
            masterVolumeSlider.onValueChanged.AddListener(OnMasterVolumeChanged);
        }

        void OnMasterVolumeChanged(float sliderValue)
        {
            Settings.MasterVolume = Mathf.Clamp01(sliderValue / VolumeSliderSteps);
            ApplyMasterVolume();
            GameSettingsRepository.Save(Settings);
        }

        void ApplyMasterVolume()
        {
            AudioListener.volume = Settings.MasterVolume;
        }

        void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // The XR rig lives in the additively-loaded Shared scene; reapply once
            // it shows up so a non-default persisted value lands on the controller.
            ApplyVignette();
            ApplyTurningMode();
            ApplyMasterVolume();
            ApplyWatchHand();
        }

        void ApplyVignette()
        {
            var controllers = FindObjectsByType<TunnelingVignetteController>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < controllers.Length; i++)
                controllers[i].enabled = Settings.VignetteEnabled;
        }

        void ApplyTurningMode()
        {
            // Smooth turning was cut from the demo (see GAME_DESIGN.md §4.2.1) —
            // the ContinuousTurnProvider stays disabled at all times. Only the
            // SnapTurnProvider responds to the user's choice.
            bool snap = Settings.TurningMode == TurningMode.Snap;

            var continuous = FindObjectsByType<ContinuousTurnProvider>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < continuous.Length; i++)
                continuous[i].enabled = false;

            var snapProviders = FindObjectsByType<SnapTurnProvider>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < snapProviders.Length; i++)
                snapProviders[i].enabled = snap;
        }

        void BindWatchHandDropdown()
        {
            if (watchHandDropdown == null)
                return;

            watchHandDropdown.ClearOptions();
            watchHandDropdown.AddOptions(new List<string>
            {
                WatchHand.Right.ToString(),
                WatchHand.Left.ToString(),
            });
            watchHandDropdown.SetValueWithoutNotify((int)Settings.WatchHand);
            watchHandDropdown.RefreshShownValue();
            watchHandDropdown.onValueChanged.AddListener(OnWatchHandChanged);
        }

        void OnWatchHandChanged(int index)
        {
            Settings.WatchHand = (WatchHand)index;
            ApplyWatchHand();
            GameSettingsRepository.Save(Settings);
        }

        void ApplyWatchHand()
        {
            var timers = FindObjectsByType<WristTimer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None);
            for (int i = 0; i < timers.Length; i++)
                timers[i].SetWatchHand(Settings.WatchHand);
        }

        void BindEchoControls()
        {
            if (echoLevelSlider != null)
            {
                echoLevelSlider.wholeNumbers = true;
                echoLevelSlider.minValue = 0f;
                echoLevelSlider.maxValue = EchoSliderSteps;
                echoLevelSlider.SetValueWithoutNotify(
                    Mathf.Round(Mathf.Clamp01(Settings.EchoLevel) * EchoSliderSteps));
                echoLevelSlider.onValueChanged.AddListener(OnEchoLevelChanged);
            }

            if (echoToggle != null)
            {
                echoToggle.SetIsOnWithoutNotify(Settings.EchoEnabled);
                echoToggle.onValueChanged.AddListener(OnEchoToggled);
            }

            RefreshEchoDisplay();
        }

        void OnEchoToggled(bool isOn)
        {
            Settings.EchoEnabled = isOn;
            RefreshEchoDisplay();
            ApplyEcho();
            GameSettingsRepository.Save(Settings);
        }

        void OnEchoLevelChanged(float sliderValue)
        {
            Settings.EchoLevel = Mathf.Clamp01(sliderValue / EchoSliderSteps);
            if (Settings.EchoEnabled) ApplyEcho();
            GameSettingsRepository.Save(Settings);
        }

        void ApplyEcho()
        {
            if (echoProcessor == null)
                echoProcessor = FindAnyObjectByType<EchoProcessor>();
            if (echoProcessor == null) return;
            echoProcessor.Apply(Settings.EchoEnabled, Settings.EchoLevel, Settings.MicrophoneDeviceName);
        }

        void RefreshEchoDisplay()
        {
            if (echoLevelSlider == null) return;
            echoLevelSlider.interactable = Settings.EchoEnabled;
            var cg = echoLevelSlider.GetComponent<CanvasGroup>();
            if (cg != null)
                cg.alpha = Settings.EchoEnabled ? 1f : 0.45f;
        }
    }
}
