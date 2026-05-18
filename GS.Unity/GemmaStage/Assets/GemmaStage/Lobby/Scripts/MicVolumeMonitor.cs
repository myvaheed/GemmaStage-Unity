using System;
using GemmaStage.Core;
using UnityEngine;
using UnityEngine.UI;

namespace GemmaStage.Lobby
{
    /// Drives a horizontal `Image.fillAmount` meter from a Microphone device's
    /// rolling buffer. Active only while the GameObject is enabled; the
    /// performance pipeline takes ownership of the device on Start Session.
    public class MicVolumeMonitor : MonoBehaviour
    {
        const int RmsWindowSamples = 1024;
        // -50 dBFS ≈ quiet room, -3 dBFS ≈ near-clip. Keep MaxDb close to
        // full-scale so a hot mic preamp doesn't peg the meter on whisper.
        const float MinDb = -50f;
        const float MaxDb = -3f;
        const float SmoothingRiseHz = 25f;
        const float SmoothingFallHz = 6f;

        [SerializeField] Image meterFill;

        [Header("Signal Detection")]
        [Tooltip("Normalized RMS (0..1) the smoothed meter must clear before the gate trips.")]
        [SerializeField, Range(0f, 1f)] float signalThreshold = 0.10f;
        [Tooltip("Seconds the smoothed RMS must stay above the threshold before MicSignalDetected fires.")]
        [SerializeField, Range(0f, 1f)] float signalDwellSeconds = 0.15f;

        AudioClip _clip;
        bool _acquired;
        string _activeDevice;
        readonly float[] _readBuffer = new float[RmsWindowSamples];
        float _smoothedLevel;
        float _signalTimer;

        // Process-lifetime cache so returning to the Lobby from Performance does
        // not force the user to re-verify the same mic. Cleared on app exit and
        // on domain reload — not persisted to PlayerPrefs because hardware state
        // (mic permissions, USB hot-plug) can change between launches.
        static string s_lastVerifiedDevice;

        /// True once the current device has produced a sustained loud-enough
        /// sample. Resets to false on every SetDevice call so each device
        /// starts unverified. Drives the Start Session gate in Lobby.
        public bool IsVerifiedForCurrentDevice { get; private set; }

        /// Fires once per SetDevice cycle when smoothed RMS clears
        /// signalThreshold for signalDwellSeconds.
        public event Action MicSignalDetected;

        public void SetMeter(Image fill) => meterFill = fill;

        public void SetDevice(string deviceName)
        {
            if (deviceName == _activeDevice && _clip != null)
                return;

            StopMonitoring();
            _activeDevice = deviceName;
            ResetSignalDetection();
            if (isActiveAndEnabled)
                StartMonitoring();
        }

        void OnEnable() => StartMonitoring();
        void OnDisable() => StopMonitoring();

        void StartMonitoring()
        {
            if (Microphone.devices.Length == 0)
                return;

            _clip = MicSource.Acquire(_activeDevice);
            _acquired = _clip != null;
            _activeDevice = MicSource.ActiveDevice;
            if (_clip == null)
                Debug.LogWarning("[MicVolumeMonitor] MicSource.Acquire returned null.", this);
        }

        void StopMonitoring()
        {
            if (_acquired)
            {
                MicSource.Release();
                _acquired = false;
            }
            _clip = null;

            _smoothedLevel = 0f;
            if (meterFill != null)
                meterFill.fillAmount = 0f;
        }

        void ResetSignalDetection()
        {
            // Restore verification when returning to Lobby with the same device
            // we previously verified — Start Session stays clickable instead of
            // forcing the user to speak loudly again every time.
            IsVerifiedForCurrentDevice =
                !string.IsNullOrEmpty(_activeDevice) && _activeDevice == s_lastVerifiedDevice;
            _signalTimer = 0f;
        }

        void Update()
        {
            if (_clip == null || meterFill == null)
                return;

            int micPos = Microphone.GetPosition(_activeDevice);
            // GetData refuses to read across the clip's wrap boundary; skip
            // the read until the head is one window past it.
            int readStart = micPos - RmsWindowSamples;
            if (readStart < 0)
                return;

            if (!_clip.GetData(_readBuffer, readStart))
                return;

            double sumSquares = 0.0;
            for (int i = 0; i < _readBuffer.Length; i++)
                sumSquares += _readBuffer[i] * _readBuffer[i];

            float rms = Mathf.Sqrt((float)(sumSquares / _readBuffer.Length));
            float db = rms > 0f ? 20f * Mathf.Log10(rms) : MinDb;
            float normalized = Mathf.InverseLerp(MinDb, MaxDb, db);

            // Asymmetric smoothing — rise quickly, fall slowly so the meter
            // tracks transients without flickering during silence.
            float hz = normalized > _smoothedLevel ? SmoothingRiseHz : SmoothingFallHz;
            float t = 1f - Mathf.Exp(-hz * Time.deltaTime);
            _smoothedLevel = Mathf.Lerp(_smoothedLevel, normalized, t);

            meterFill.fillAmount = _smoothedLevel;

            if (!IsVerifiedForCurrentDevice)
            {
                if (_smoothedLevel >= signalThreshold)
                    _signalTimer += Time.deltaTime;
                else
                    _signalTimer = 0f;

                if (_signalTimer >= signalDwellSeconds)
                {
                    IsVerifiedForCurrentDevice = true;
                    s_lastVerifiedDevice = _activeDevice;
                    MicSignalDetected?.Invoke();
                }
            }
        }
    }
}
