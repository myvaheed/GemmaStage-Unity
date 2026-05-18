using UnityEngine;

namespace GemmaStage.Core
{
    /// <summary>
    /// Mic-to-headphone monitor. Pulls samples from <see cref="MicSource"/> on
    /// the main thread into a ring buffer, then emits them on the audio thread
    /// via <c>OnAudioFilterRead</c>. The attached <see cref="AudioReverbFilter"/>
    /// adds the Concerthall character downstream.
    /// </summary>
    [RequireComponent(typeof(AudioSource))]
    public class EchoProcessor : MonoBehaviour
    {
        // Samples kept between the mic write head and the audio-thread read
        // head. Tighter than the AudioSource path can sustain; drift recovery
        // in OnAudioFilterRead keeps it stable. 128 @ 48 kHz ≈ 2.7 ms.
        const int MicReadMarginSamples = 128;

        // Must comfortably exceed (frame interval + DSP buffer + jitter).
        // 4096 @ 48 kHz ≈ 85 ms.
        const int RingBufferSamples = 4096;

        const float ReverbLevelMillibels = -3000f;

        // Slider 0..1 maps to output gain 0..MaxOutputGain. Mic + reverb sits
        // noticeably louder than the rest of the scene at unity gain.
        const float MaxOutputGain = 0.5f;

        AudioSource _audioSource;
        AudioReverbFilter _reverbFilter;
        bool _micAcquired;

        // Written by Update (main thread), read by OnAudioFilterRead (audio
        // thread). _writeIndex is volatile so the audio thread sees the latest
        // writer position and the buffer writes that preceded it.
        readonly float[] _ringBuffer = new float[RingBufferSamples];
        readonly float[] _micScratch = new float[RingBufferSamples];
        volatile int _writeIndex;
        int _micReadIndex;
        int _audioReadIndex;
        bool _audioReadPrimed;

        volatile bool _outputEnabled;
        float _outputVolume;

        // DSP buffer size dominates mic-to-headphone latency. Default is
        // typically 1024 (~21 ms @ 48 kHz); 256 is "Best Latency". Reset()
        // reinitializes the audio engine, so this must run before any
        // AudioSource starts.
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void ConfigureLowLatencyAudio()
        {
            var config = AudioSettings.GetConfiguration();
            if (config.dspBufferSize <= 256) return;
            config.dspBufferSize = 256;
            AudioSettings.Reset(config);
        }

        void Awake()
        {
            _audioSource = GetComponent<AudioSource>();

            _reverbFilter = GetComponent<AudioReverbFilter>();
            if (_reverbFilter == null)
                _reverbFilter = gameObject.AddComponent<AudioReverbFilter>();

            _reverbFilter.reverbPreset = AudioReverbPreset.Concerthall;
            _reverbFilter.enabled = false;

            // OnAudioFilterRead only fires while the AudioSource is playing;
            // a silent looping clip drives the callback.
            int sampleRate = AudioSettings.outputSampleRate > 0
                ? AudioSettings.outputSampleRate
                : 48000;
            var silence = AudioClip.Create("EchoSilence", sampleRate, 1, sampleRate, false);
            _audioSource.clip = silence;
            _audioSource.loop = true;
            _audioSource.playOnAwake = false;
        }

        public void Apply(bool enabled, float level, string deviceName)
        {
            level = Mathf.Clamp01(level);

            _reverbFilter.reverbPreset = AudioReverbPreset.Concerthall;
            _reverbFilter.reverbLevel = ReverbLevelMillibels;
            _reverbFilter.enabled = enabled;

            _outputVolume = level * MaxOutputGain;
            _outputEnabled = enabled;

            if (enabled) StartMonitoring(deviceName);
            else StopMonitoring();
        }

        void StartMonitoring(string device)
        {
            var clip = MicSource.Acquire(device);
            if (clip == null) return;

            // Idempotent re-apply (slider drag): keep the existing pipeline.
            if (_micAcquired) return;

            _micAcquired = true;
            _micReadIndex = MicSource.Position;
            _writeIndex = 0;
            _audioReadIndex = 0;
            _audioReadPrimed = false;

            if (!_audioSource.isPlaying) _audioSource.Play();
        }

        void StopMonitoring()
        {
            if (_audioSource.isPlaying) _audioSource.Stop();
            ReleaseMic();
        }

        void ReleaseMic()
        {
            if (!_micAcquired) return;
            MicSource.Release();
            _micAcquired = false;
        }

        void Update()
        {
            if (!_micAcquired) return;
            var clip = MicSource.Clip;
            if (clip == null) return;

            int micLength = MicSource.LengthSamples;
            int micPos = MicSource.Position;
            int available = (micPos - _micReadIndex + micLength) % micLength;
            if (available <= 0) return;
            if (available > _micScratch.Length) available = _micScratch.Length;

            // Split the read at the clip's wrap boundary — GetData refuses to
            // span it in a single call.
            int firstChunk = Mathf.Min(available, micLength - _micReadIndex);
            clip.GetData(_micScratch, _micReadIndex);
            CopyToRing(_micScratch, firstChunk);
            if (firstChunk < available)
            {
                clip.GetData(_micScratch, 0);
                CopyToRing(_micScratch, available - firstChunk);
            }

            _micReadIndex = (_micReadIndex + available) % micLength;
        }

        void CopyToRing(float[] src, int count)
        {
            int w = _writeIndex;
            for (int i = 0; i < count; i++)
            {
                _ringBuffer[w] = src[i];
                w++;
                if (w >= RingBufferSamples) w = 0;
            }
            _writeIndex = w;
        }

        // Audio thread.
        void OnAudioFilterRead(float[] data, int channels)
        {
            if (!_outputEnabled || !_micAcquired)
            {
                System.Array.Clear(data, 0, data.Length);
                return;
            }

            int writeIndex = _writeIndex;
            int frames = data.Length / channels;
            int filled = (writeIndex - _audioReadIndex + RingBufferSamples) % RingBufferSamples;

            // Mic clock and output clock are independent oscillators and drift
            // over time. If the writer falls behind, the reader catches up and
            // starts replaying stale ring data (clicks). Re-anchor the read
            // head behind the writer when that's about to happen — costs one
            // pitch-jump per recovery instead of repeated glitches.
            if (!_audioReadPrimed || filled < frames + MicReadMarginSamples / 2)
            {
                if (filled < MicReadMarginSamples)
                {
                    System.Array.Clear(data, 0, data.Length);
                    return;
                }
                _audioReadIndex = (writeIndex - MicReadMarginSamples + RingBufferSamples) % RingBufferSamples;
                _audioReadPrimed = true;
            }

            int r = _audioReadIndex;
            float volume = _outputVolume;
            for (int f = 0; f < frames; f++)
            {
                float sample = _ringBuffer[r] * volume;
                int baseIdx = f * channels;
                for (int c = 0; c < channels; c++)
                    data[baseIdx + c] = sample;
                r++;
                if (r >= RingBufferSamples) r = 0;
            }
            _audioReadIndex = r;
        }

        void OnDestroy() => ReleaseMic();
    }
}
