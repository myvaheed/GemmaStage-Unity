using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using System;
using System.Threading;

namespace GemmaStage.Session.Perceptor;

// Per-chunk ASR conversation. Each SendAudio call creates a fresh
// ConversationHandle so the model never sees a prior assistant transcript.
// This eliminates the regurgitation pattern that the long-lived Perceptor
// conversation suffered from (GS-95): with chat history retained, the model
// would copy the previous chunk's transcript and extend it instead of
// transcribing the new audio in isolation.
public sealed class AsrConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public AsrConversation(EngineHandle engine, string language = "English", Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            AsrPrompts.BuildSystem(language),
            AsrPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, AsrPrompts.EnableThinking);
    }

    // Per-call conversation: allocate, send, parse, dispose. The system
    // prompt prefill cost is small (ASR-only instructions + one tool def)
    // compared to audio decode latency, and this is the cheapest way to
    // guarantee the model sees no prior chat history.
    public PerceptorTurnResult SendAudio(byte[] wav, TimeSpan? inputDuration = null)
    {
        Guard.NotNull(wav);
        if (wav.Length == 0)
        {
            throw new ArgumentException("Audio payload cannot be empty.", nameof(wav));
        }
        if (inputDuration is { } duration && duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(inputDuration),
                inputDuration,
                "Input duration cannot be negative.");
        }
        ThrowIfDisposed();

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute("Asr", _config, AsrPrompts.EnableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendAudio(
                conversation,
                AsrPrompts.UserPrompt,
                wav);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return AsrResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (!parse.IsAudio || parse.Audio is null)
        {
            Warn($"ASR audio turn {turn} produced \"{parse.ToolName}\" instead of audio observation.");
            return new PerceptorTurnResult(turn, timestamp, inputDuration, parse, benchmark, responseJson);
        }

        var audio = NormalizeAudioOutput(turn, parse.Audio);
        if (!ReferenceEquals(audio, parse.Audio))
        {
            parse = parse with { Audio = audio };
        }

        return new PerceptorTurnResult(turn, timestamp, inputDuration, parse, benchmark, responseJson);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _config.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private PerceptorAudioOutput NormalizeAudioOutput(long turn, PerceptorAudioOutput audio)
    {
        if (audio.Clarity != PerceptorClarity.Messy)
        {
            if (audio.Emotion is null)
            {
                Warn($"ASR turn {turn} reported clarity=normal but omitted emotion.");
            }

            if (audio.Grammar is null)
            {
                Warn($"ASR turn {turn} reported clarity=normal but omitted grammar.");
            }

            // Post-hoc override: trust the transcript's trailing punctuation,
            // not the model's self-report. With per-chunk isolation the model
            // judges chunk_completed purely from the audio's tail (which is
            // always padded with silence by the chunker), so its native answer
            // is heavily biased toward true. Punctuation is what the system
            // prompt asked it to emit anyway, so this is just enforcing the
            // contract instead of trusting the boolean.
            var derived = DeriveChunkCompletedFromTranscript(audio.Transcript);
            return derived == audio.ChunkCompleted
                ? audio
                : audio with { ChunkCompleted = derived };
        }

        var emotion = audio.Emotion;
        var grammar = audio.Grammar;
        var notes = audio.Notes;
        var chunkCompleted = audio.ChunkCompleted;
        var changed = false;

        if (emotion is not null)
        {
            Warn($"ASR turn {turn} reported clarity=messy but emitted emotion={emotion}; clearing it.");
            emotion = null;
            changed = true;
        }

        if (grammar is not null)
        {
            Warn($"ASR turn {turn} reported clarity=messy but set grammar={grammar}; clearing it.");
            grammar = null;
            changed = true;
        }

        if (notes.Count > 0)
        {
            Warn($"ASR turn {turn} reported clarity=messy but emitted {notes.Count} note(s); discarding.");
            notes = Array.Empty<string>();
            changed = true;
        }

        if (chunkCompleted)
        {
            // Messy chunks are never "completed" - the schema mandates false.
            chunkCompleted = false;
            changed = true;
        }

        if (!changed)
        {
            return audio;
        }

        return audio with
        {
            Emotion = emotion,
            Grammar = grammar,
            Notes = notes,
            ChunkCompleted = chunkCompleted,
        };
    }

    // Closing quotes/brackets/whitespace are stripped before checking the
    // terminator, so transcripts like ' she said: "go home." ' still resolve
    // to completed. Empty or punctuation-only transcripts resolve to false.
    internal static bool DeriveChunkCompletedFromTranscript(string transcript)
    {
        if (string.IsNullOrEmpty(transcript))
        {
            return false;
        }

        var span = transcript.AsSpan().TrimEnd();
        while (span.Length > 0 && IsTrailingWrapper(span[^1]))
        {
            span = span[..^1];
        }

        if (span.Length == 0)
        {
            return false;
        }

        var last = span[^1];
        return last == '.' || last == '?' || last == '!';
    }

    private static bool IsTrailingWrapper(char c)
    {
        return c switch
        {
            '"' or '\'' or ')' or ']' or '}' or '\u00BB' => true,
            // Unicode smart quotes: U+201D right double, U+2019 right single.
            '\u201D' or '\u2019' => true,
            _ => false,
        };
    }

    private static BenchmarkSnapshot ReadBenchmark(ConversationHandle conversation)
    {
        try
        {
            using var info = GemmaStageNative.ConversationGetBenchmarkInfo(conversation);
            return GemmaStageNative.GetBenchmarkSnapshot(info, conversation);
        }
        catch
        {
            return BenchmarkSnapshot.Empty;
        }
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }
}

public sealed record PerceptorTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    TimeSpan? InputDuration,
    PerceptorParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);

internal static class PerceptorEnumExtensions
{
    public static string ToWireString(this PerceptorClarity clarity)
    {
        return clarity switch
        {
            PerceptorClarity.Messy => "messy",
            PerceptorClarity.Normal => "normal",
            _ => throw new ArgumentOutOfRangeException(nameof(clarity), clarity, null),
        };
    }

    public static string ToWireString(this PerceptorGrammar grammar)
    {
        return grammar switch
        {
            PerceptorGrammar.Poor => "poor",
            PerceptorGrammar.Moderate => "moderate",
            PerceptorGrammar.Good => "good",
            PerceptorGrammar.Excellent => "excellent",
            _ => throw new ArgumentOutOfRangeException(nameof(grammar), grammar, null),
        };
    }

    public static string ToWireString(this PerceptorEmotion emotion)
    {
        return emotion switch
        {
            PerceptorEmotion.Calm => "calm",
            PerceptorEmotion.Enthusiastic => "enthusiastic",
            PerceptorEmotion.Tense => "tense",
            PerceptorEmotion.Uncertain => "uncertain",
            _ => throw new ArgumentOutOfRangeException(nameof(emotion), emotion, null),
        };
    }
}
