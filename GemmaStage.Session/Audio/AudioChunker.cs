using System;
using System.Collections.Generic;

namespace GemmaStage.Session.Audio;

public class AudioChunk
{
    public int StartSample { get; set; }
    public int EndSample { get; set; }
    public char[] Symbols { get; set; } = Array.Empty<char>();
    public List<AudioChunkSpan> Spans { get; set; } = new();
}

public sealed record AudioChunkSpan(int StartSample, int EndSample);

public class AudioChunkerConfig
{
    public TimeSpan TargetChunkWindowX { get; set; } = TimeSpan.FromSeconds(15);
    public TimeSpan TrailingSilenceY { get; set; } = TimeSpan.FromMilliseconds(300);
    public TimeSpan WindowMsC { get; set; } = TimeSpan.FromMilliseconds(300);
}

public class AudioChunker
{
    private readonly AudioChunkerConfig _config;

    public AudioChunker(AudioChunkerConfig config)
    {
        _config = config;
    }

    public List<AudioChunk> Chunk(char[] symbols, int sampleRate)
    {
        var chunks = new List<AudioChunk>();
        int ySymbols = (int)Math.Ceiling(_config.TrailingSilenceY.TotalMilliseconds / _config.WindowMsC.TotalMilliseconds);
        int xSymbols = (int)Math.Ceiling(_config.TargetChunkWindowX.TotalMilliseconds / _config.WindowMsC.TotalMilliseconds);
        int samplesPerSymbol = (int)(sampleRate * (_config.WindowMsC.TotalMilliseconds / 1000.0));

        ySymbols = Math.Max(1, ySymbols);
        xSymbols = Math.Max(1, xSymbols);
        samplesPerSymbol = Math.Max(1, samplesPerSymbol);

        var activeSymbols = new List<char>();
        var activeSpans = new List<AudioChunkSpan>();
        int pendingSilenceStart = -1;
        int pendingSilenceEnd = -1;
        int silenceRunLength = 0;
        int voiceCountInChunk = 0;
        bool hasActiveChunk = false;

        void AppendSymbol(int symbolIndex)
        {
            activeSymbols.Add(symbols[symbolIndex]);
            AppendSpan(symbolIndex);
        }

        void AppendSpan(int symbolIndex)
        {
            int startSample = symbolIndex * samplesPerSymbol;
            int endSample = (symbolIndex + 1) * samplesPerSymbol - 1;

            if (activeSpans.Count > 0 && activeSpans[^1].EndSample + 1 == startSample)
            {
                var previous = activeSpans[^1];
                activeSpans[^1] = previous with { EndSample = endSample };
                return;
            }

            activeSpans.Add(new AudioChunkSpan(startSample, endSample));
        }

        void BeginChunk(int voiceIndex)
        {
            hasActiveChunk = true;
            voiceCountInChunk = 0;

            int leadingSilence = voiceIndex - 1;
            if (leadingSilence >= 0 && symbols[leadingSilence] == VolumeClassifier.Silence)
            {
                AppendSymbol(leadingSilence);
            }
        }

        void FlushPendingSilenceForVoice()
        {
            if (pendingSilenceStart < 0)
            {
                return;
            }

            AppendSymbol(pendingSilenceStart);
            if (pendingSilenceEnd != pendingSilenceStart)
            {
                AppendSymbol(pendingSilenceEnd);
            }

            ResetPendingSilence();
        }

        void KeepSilence(int symbolIndex)
        {
            if (pendingSilenceStart < 0)
            {
                pendingSilenceStart = symbolIndex;
            }

            pendingSilenceEnd = symbolIndex;
            silenceRunLength++;
        }

        void EmitChunk(bool includeTrailingSilence)
        {
            if (includeTrailingSilence && pendingSilenceStart >= 0)
            {
                AppendSymbol(pendingSilenceStart);
            }

            chunks.Add(new AudioChunk
            {
                StartSample = activeSpans[0].StartSample,
                EndSample = activeSpans[^1].EndSample,
                Symbols = activeSymbols.ToArray(),
                Spans = activeSpans.ToList(),
            });

            ResetChunk();
        }

        void ResetChunk()
        {
            activeSymbols.Clear();
            activeSpans.Clear();
            ResetPendingSilence();
            voiceCountInChunk = 0;
            hasActiveChunk = false;
        }

        void ResetPendingSilence()
        {
            pendingSilenceStart = -1;
            pendingSilenceEnd = -1;
            silenceRunLength = 0;
        }

        for (int i = 0; i < symbols.Length; i++)
        {
            if (symbols[i] == VolumeClassifier.Voice)
            {
                if (!hasActiveChunk)
                {
                    BeginChunk(i);
                }

                FlushPendingSilenceForVoice();
                AppendSymbol(i);
                voiceCountInChunk++;
                continue;
            }

            if (!hasActiveChunk)
            {
                continue;
            }

            KeepSilence(i);
            if (voiceCountInChunk >= xSymbols && silenceRunLength >= ySymbols)
            {
                EmitChunk(includeTrailingSilence: true);
            }
        }

        if (hasActiveChunk)
        {
            EmitChunk(includeTrailingSilence: true);
        }

        return chunks;
    }
}
