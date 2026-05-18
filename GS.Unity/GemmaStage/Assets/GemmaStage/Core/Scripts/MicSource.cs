using UnityEngine;

namespace GemmaStage.Core
{
    /// <summary>
    /// Process-wide refcounted owner of the OS microphone device. Consumers
    /// (<c>MicVolumeMonitor</c>, <c>EchoProcessor</c>, future AudioPipeline)
    /// share one underlying <see cref="Microphone"/> recording instead of
    /// stomping each other with redundant Start/End calls. The OS device is
    /// only ended when the last consumer releases it.
    /// </summary>
    public static class MicSource
    {
        public const int DefaultLengthSeconds = 1;
        public const int DefaultSampleRate = 44100;

        static int _refCount;
        static string _device;
        static AudioClip _clip;

        public static string ActiveDevice => _device;
        public static AudioClip Clip => _clip;
        public static int Position => _device != null ? Microphone.GetPosition(_device) : 0;
        public static int SampleRate => _clip != null ? _clip.frequency : DefaultSampleRate;
        public static int Channels => _clip != null ? _clip.channels : 1;
        public static int LengthSamples => _clip != null ? _clip.samples : DefaultLengthSeconds * DefaultSampleRate;

        /// <summary>
        /// Acquire the shared mic clip for the requested device. Returns null
        /// if no mic devices are present. Each successful Acquire must be
        /// paired with one Release.
        /// </summary>
        public static AudioClip Acquire(string requestedDevice)
        {
            if (Microphone.devices.Length == 0)
                return null;

            var device = ResolveDevice(requestedDevice);

            if (_clip != null && _device == device)
            {
                _refCount++;
                return _clip;
            }

            // Switching devices invalidates any previous consumer's clip
            // handle — they must re-acquire if they want the new device.
            if (_clip != null)
                ForceEnd();

            _device = device;
            // Match the engine's output rate so consumers can pipe samples to
            // OnAudioFilterRead without resampling.
            int sampleRate = AudioSettings.outputSampleRate > 0
                ? AudioSettings.outputSampleRate
                : DefaultSampleRate;
            _clip = Microphone.Start(device, true, DefaultLengthSeconds, sampleRate);
            if (_clip == null)
            {
                Debug.LogWarning($"[MicSource] Microphone.Start returned null for device '{device}'.");
                _device = null;
                return null;
            }
            _refCount = 1;
            return _clip;
        }

        public static void Release()
        {
            if (_refCount <= 0) return;
            _refCount--;
            if (_refCount == 0)
                ForceEnd();
        }

        static void ForceEnd()
        {
            if (_device != null && Microphone.IsRecording(_device))
                Microphone.End(_device);
            _clip = null;
            _device = null;
            _refCount = 0;
        }

        static string ResolveDevice(string requested)
        {
            var devices = Microphone.devices;
            if (string.IsNullOrEmpty(requested))
                return devices[0];
            for (int i = 0; i < devices.Length; i++)
                if (devices[i] == requested)
                    return devices[i];
            return devices[0];
        }
    }
}
