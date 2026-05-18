using System;
using System.Collections.Generic;
using System.IO;
using GemmaStage.Session.Audio;
using UnityEngine;

namespace GemmaStage.Core
{
    // Mic capture → volume classifier → chunker → ChunkReady(wav, duration).
    //
    // Owns the shared mic via MicSource (refcounted with EchoProcessor and the
    // Lobby's MicVolumeMonitor). SessionLayer subscribes to ChunkReady at Begin
    // and forwards bytes into Session.PushAudio on its worker thread; this
    // class stays on the Unity main thread.
    //
    // The classifier and chunker are reused unchanged from the session module
    // (GemmaStage.Session.Audio). On each pump tick the classifier+chunker run
    // over the accumulated buffer. AudioChunker has two emission paths: a
    // steady-state path that fires when accumulated voice >= xSymbols AND
    // trailing silence >= ySymbols, and a stream-end fallback that emits
    // whatever is still active when its input array ends. In streaming use
    // the fallback fires every tick — we suppress it by counting voice
    // symbols in the returned chunk: only chunks with voice >= xSymbols
    // could have come from the steady-state path. Partial chunks are held
    // back so voice keeps accumulating across pump ticks; StopCapture
    // flushes whatever's left.
    public class AudioPipeline : MonoBehaviour
    {
        // PoC defaults (GemmaStage.Session.PoC/PocOptions.cs).
        const int ChunkerXSeconds       = 15;
        const int ChunkerYMilliseconds  = 300;
        const int ChunkerCMilliseconds  = 300;
        const double VolumeThresholdDb  = -45.0;

        // Pump cadence — matches the chunker's C window so each tick adds one
        // symbol of evidence.
        const float PumpIntervalSec = 0.3f;

        public string    CurrentDeviceName { get; private set; }
        public AudioClip CurrentMicClip    { get; private set; }
        public bool      IsCapturing       { get; private set; }
        public bool      IsPaused          { get; private set; }
        public int       SampleRate        { get; private set; }

        public event Action<byte[], TimeSpan> ChunkReady;

        readonly List<short> _buffer = new();
        readonly object _bufferLock = new();
        readonly VolumeClassifierConfig _classifierConfig = new VolumeClassifierConfig
        {
            WindowMs = ChunkerCMilliseconds,
            AbsoluteThresholdDb = VolumeThresholdDb,
        };
        readonly AudioChunkerConfig _chunkerConfig = new AudioChunkerConfig
        {
            TargetChunkWindowX = TimeSpan.FromSeconds(ChunkerXSeconds),
            TrailingSilenceY   = TimeSpan.FromMilliseconds(ChunkerYMilliseconds),
            WindowMsC          = TimeSpan.FromMilliseconds(ChunkerCMilliseconds),
        };
        AudioChunker _chunker;
        int _lastMicPos;
        float _nextPumpAt;

        public void StartCapture(string deviceName)
        {
            if (IsCapturing)
            {
                Debug.LogWarning("[AudioPipeline] StartCapture called while already capturing. Ignoring.");
                return;
            }

            var clip = MicSource.Acquire(deviceName);
            if (clip == null)
            {
                Debug.LogError("[AudioPipeline] No microphone available — capture not started.");
                return;
            }

            CurrentMicClip    = clip;
            CurrentDeviceName = MicSource.ActiveDevice;
            SampleRate        = MicSource.SampleRate;
            _chunker          = new AudioChunker(_chunkerConfig);
            _lastMicPos       = MicSource.Position;
            _nextPumpAt       = Time.unscaledTime + PumpIntervalSec;
            lock (_bufferLock) _buffer.Clear();
            IsCapturing = true;
            IsPaused    = false;

            Debug.Log($"[AudioPipeline] StartCapture device='{CurrentDeviceName}' rate={SampleRate}Hz channels={clip.channels}");
        }

        public void StopCapture()
        {
            if (!IsCapturing) return;

            // Final pass — drain anything still in the buffer, including the
            // trailing active chunk if voice was still in progress.
            DrainMic();
            RunChunker(emitTrailingActive: true);

            MicSource.Release();
            CurrentMicClip    = null;
            CurrentDeviceName = null;
            SampleRate        = 0;
            _chunker          = null;
            IsCapturing = false;
            IsPaused    = false;
            lock (_bufferLock) _buffer.Clear();
        }

        // Drop the buffer and stop draining the mic so a long pause does not
        // flush a giant chunk on resume. The mic device stays open.
        public void Pause()
        {
            if (!IsCapturing || IsPaused) return;
            IsPaused = true;
            lock (_bufferLock) _buffer.Clear();
        }

        public void Resume()
        {
            if (!IsCapturing || !IsPaused) return;
            // Resync cursor so we don't replay samples captured during pause.
            _lastMicPos = MicSource.Position;
            _nextPumpAt = Time.unscaledTime + PumpIntervalSec;
            IsPaused = false;
        }

        void Update()
        {
            if (!IsCapturing || IsPaused) return;
            DrainMic();
            if (Time.unscaledTime >= _nextPumpAt)
            {
                _nextPumpAt = Time.unscaledTime + PumpIntervalSec;
                RunChunker(emitTrailingActive: false);
            }
        }

        void DrainMic()
        {
            var clip = CurrentMicClip;
            if (clip == null) return;

            int lengthSamples = clip.samples;
            if (lengthSamples <= 0) return;

            int currentPos = MicSource.Position;
            int delta = currentPos - _lastMicPos;
            if (delta < 0) delta += lengthSamples;
            if (delta == 0) return;

            int channels = clip.channels;
            var floats = new float[delta * channels];
            // GetData reads with wrap-around from the offset. MicSource's clip
            // is a loop recording so this is safe across the boundary.
            clip.GetData(floats, _lastMicPos);

            lock (_bufferLock)
            {
                for (int i = 0; i < delta; i++)
                {
                    float f;
                    if (channels == 1)
                    {
                        f = floats[i];
                    }
                    else
                    {
                        float sum = 0;
                        for (int c = 0; c < channels; c++) sum += floats[i * channels + c];
                        f = sum / channels;
                    }
                    if (f > 1f) f = 1f;
                    else if (f < -1f) f = -1f;
                    _buffer.Add((short)(f * 32767f));
                }
            }

            _lastMicPos = currentPos;
        }

        void RunChunker(bool emitTrailingActive)
        {
            short[] snapshot;
            lock (_bufferLock)
            {
                if (_buffer.Count == 0) return;
                snapshot = _buffer.ToArray();
            }

            if (_chunker == null) return;

            var wav = new WavData(SampleRate, snapshot);
            char[] symbols;
            try { symbols = VolumeClassifier.Classify(wav, _classifierConfig); }
            catch (Exception ex) { Debug.LogException(ex); return; }

            List<AudioChunk> chunks;
            try { chunks = _chunker.Chunk(symbols, SampleRate); }
            catch (Exception ex) { Debug.LogException(ex); return; }

            if (chunks.Count == 0) return;

            int xSymbols  = Math.Max(1, (int)Math.Ceiling((double)(ChunkerXSeconds * 1000) / ChunkerCMilliseconds));
            int bufferLen = snapshot.Length;

            int maxEnd = -1;
            for (int i = 0; i < chunks.Count; i++)
            {
                var ch = chunks[i];
                bool isLast = i == chunks.Count - 1;
                // Only the last chunk can be a stream-end fallback emission
                // (AudioChunker emits whatever is active at end-of-array even
                // if X seconds of voice weren't reached). Distinguish it from
                // the steady-state path by counting voice symbols: a
                // steady-state emit always has voice >= xSymbols. Hold partial
                // chunks back so voice keeps accumulating across pump ticks.
                bool completed = !isLast || CountVoiceSymbols(ch) >= xSymbols;
                if (!completed && !emitTrailingActive) continue;

                var bytes = ExtractChunkWav(wav, ch);
                if (bytes.Length == 0) continue;

                int chunkSamples = CountChunkSamples(wav, ch);
                var duration = TimeSpan.FromSeconds((double)chunkSamples / Math.Max(1, SampleRate));

                try { ChunkReady?.Invoke(bytes, duration); }
                catch (Exception ex) { Debug.LogException(ex); }

                if (ch.EndSample > maxEnd) maxEnd = ch.EndSample;
            }

            if (maxEnd >= 0)
            {
                int dropCount = Math.Min(maxEnd + 1, bufferLen);
                lock (_bufferLock)
                {
                    if (_buffer.Count >= dropCount) _buffer.RemoveRange(0, dropCount);
                    else _buffer.Clear();
                }
            }
        }

        static byte[] ExtractChunkWav(WavData wav, AudioChunk chunk)
        {
            int numSamples = CountChunkSamples(wav, chunk);
            if (numSamples <= 0) return Array.Empty<byte>();

            int dataSize = numSamples * 2;
            int fileSize = 44 + dataSize;

            using var ms = new MemoryStream(fileSize);
            using var w  = new BinaryWriter(ms);

            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            w.Write(fileSize - 8);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            w.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            w.Write(16);
            w.Write((short)1);
            w.Write((short)1);
            w.Write(wav.SampleRate);
            w.Write(wav.SampleRate * 2);
            w.Write((short)2);
            w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            w.Write(dataSize);

            foreach (var span in chunk.Spans)
            {
                int start = Math.Max(0, span.StartSample);
                int end   = Math.Min(wav.Samples.Length - 1, span.EndSample);
                for (int i = start; i <= end; i++)
                    w.Write(wav.Samples[i]);
            }

            return ms.ToArray();
        }

        static int CountVoiceSymbols(AudioChunk chunk)
        {
            if (chunk?.Symbols == null) return 0;
            int n = 0;
            for (int i = 0; i < chunk.Symbols.Length; i++)
                if (chunk.Symbols[i] == VolumeClassifier.Voice) n++;
            return n;
        }

        static int CountChunkSamples(WavData wav, AudioChunk chunk)
        {
            int total = 0;
            foreach (var span in chunk.Spans)
            {
                int start = Math.Max(0, span.StartSample);
                int end   = Math.Min(wav.Samples.Length - 1, span.EndSample);
                if (end >= start) total += end - start + 1;
            }
            return total;
        }
    }
}
