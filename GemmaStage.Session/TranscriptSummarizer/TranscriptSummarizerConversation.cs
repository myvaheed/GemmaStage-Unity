using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using System.Text;

namespace GemmaStage.Session.TranscriptSummarizer;

public sealed record TranscriptSummarizerTurnResult(
    long TurnSequence,
    DateTimeOffset Timestamp,
    TranscriptSummarizerParseResult Parse,
    BenchmarkSnapshot Benchmark,
    string? RawResponseJson);

public sealed class TranscriptSummarizerConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private readonly string _role;
    private readonly bool _initialThinking;

    private long _turnSequence;
    private bool _disposed;

    private TranscriptSummarizerConversation(
        EngineHandle engine,
        ConversationConfigHandle config,
        Action<string>? warn,
        string role,
        bool initialThinking)
    {
        _engine = engine;
        _config = config;
        _warn = warn;
        _role = role;
        _initialThinking = initialThinking;
    }

    public static TranscriptSummarizerConversation CreateMapRetelling(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            TranscriptSummarizerMapRetellingPrompts.System,
            TranscriptSummarizerMapRetellingPrompts.EnableThinking,
            TranscriptSummarizerMapRetellingPrompts.ToolsJson,
            warn,
            "TranscriptSummarizer.MapRetelling");
    }

    public static TranscriptSummarizerConversation CreateMapSignals(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(
            engine,
            TranscriptSummarizerMapSignalsPrompts.System,
            TranscriptSummarizerMapSignalsPrompts.EnableThinking,
            TranscriptSummarizerMapSignalsPrompts.ToolsJson,
            warn,
            "TranscriptSummarizer.MapSignals");
    }

    public static TranscriptSummarizerConversation CreateReduce(
        EngineHandle engine,
        Action<string>? warn = null)
    {
        return Create(engine, TranscriptSummarizerReducePrompts.System, TranscriptSummarizerReducePrompts.EnableThinking, TranscriptSummarizerReducePrompts.ToolsJson, warn, "TranscriptSummarizer.Reduce");
    }

    public TranscriptSummarizerTurnResult SendChunkForRetelling(
        string transcriptChunk,
        int chunkIndex,
        int totalChunks,
        string? previousRetelling = null,
        IReadOnlyList<QaPromptPair>? qaPairs = null)
    {
        Guard.NotNullOrWhiteSpace(transcriptChunk);
        ValidateChunkIndex(chunkIndex, totalChunks);
        ThrowIfDisposed();

        var prompt = BuildRetellingPrompt(transcriptChunk, chunkIndex, totalChunks, previousRetelling, qaPairs);
        return RunSendText(prompt, expectMapRetelling: true);
    }

    public TranscriptSummarizerTurnResult SendChunkForSignals(
        string transcriptChunk,
        IReadOnlyList<TranscriptSummarizerMapOutput> priorSummaries,
        int chunkIndex,
        int totalChunks)
    {
        Guard.NotNullOrWhiteSpace(transcriptChunk);
        Guard.NotNull(priorSummaries);
        ValidateChunkIndex(chunkIndex, totalChunks);
        ThrowIfDisposed();

        var prompt = BuildSignalsPrompt(transcriptChunk, priorSummaries, chunkIndex, totalChunks);
        return RunSendText(prompt, expectMapSignals: true);
    }

    public TranscriptSummarizerTurnResult SendReducePrompt(string orderedMapOutputs)
    {
        Guard.NotNullOrWhiteSpace(orderedMapOutputs);
        ThrowIfDisposed();

        var prompt = TranscriptSummarizerReducePrompts.UserTemplate
            .Replace("{map_outputs}", orderedMapOutputs.TrimEnd())
            .Replace("{reduce_tool_name}", TranscriptSummarizerSchemas.ReduceToolName)
            .TrimEnd();

        return RunSendText(prompt, expectReduce: true);
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

    internal static string BuildRetellingPrompt(
        string transcriptChunk,
        int chunkIndex,
        int totalChunks,
        string? previousRetelling = null,
        IReadOnlyList<QaPromptPair>? qaPairs = null)
    {
        return TranscriptSummarizerMapRetellingPrompts.UserTemplate
            .Replace("{previous_retelling}", BuildPreviousRetellingBlock(previousRetelling))
            .Replace("{qa_rounds_block}", BuildQaRoundsBlock(qaPairs))
            .Replace("{chunk_header}", BuildChunkHeader(chunkIndex, totalChunks, signalsContext: false))
            .Replace("{chunk_text}", transcriptChunk.TrimEnd())
            .Replace("{tool_name}", TranscriptSummarizerSchemas.MapRetellingToolName)
            .TrimEnd();
    }

    private static string BuildPreviousRetellingBlock(string? previousRetelling)
    {
        return string.IsNullOrWhiteSpace(previousRetelling)
            ? "  (none — this is the first chunk)"
            : "  " + previousRetelling!.Trim();
    }

    private static string BuildQaRoundsBlock(IReadOnlyList<QaPromptPair>? qaPairs)
    {
        if (qaPairs is null || qaPairs.Count == 0)
        {
            return "  (none — no Q&A rounds occurred during this chunk)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < qaPairs.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  - Question: \"").Append(qaPairs[i].Question.Trim()).Append("\"")
              .AppendLine()
              .Append("    Speaker's spoken answer (raw transcript span): ")
              .Append(string.IsNullOrWhiteSpace(qaPairs[i].Answer) ? "(no speech captured)" : qaPairs[i].Answer.Trim());
        }
        return sb.ToString();
    }

    internal static string BuildSignalsPrompt(
        string transcriptChunk,
        IReadOnlyList<TranscriptSummarizerMapOutput> priorSummaries,
        int chunkIndex,
        int totalChunks)
    {
        return TranscriptSummarizerMapSignalsPrompts.UserTemplate
            .Replace("{prior_chunk_retellings}", BuildPriorChunkRetellings(priorSummaries))
            .Replace("{chunk_header}", BuildChunkHeader(chunkIndex, totalChunks, signalsContext: true))
            .Replace("{chunk_text}", transcriptChunk.TrimEnd())
            .Replace("{tool_name}", TranscriptSummarizerSchemas.MapSignalsToolName)
            .TrimEnd();
    }

    private TranscriptSummarizerTurnResult RunSendText(
        string prompt,
        bool expectMapRetelling = false,
        bool expectMapSignals = false,
        bool expectReduce = false)
    {
        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute(_role, _config, _initialThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, prompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return TranscriptSummarizerResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (expectMapRetelling && !parse.IsMapRetelling)
        {
            Warn($"TranscriptSummarizer MAP-retelling turn {turn} produced \"{parse.ToolName}\" instead of report_chunk_retelling.");
        }
        else if (expectMapSignals && !parse.IsMapSignals)
        {
            Warn($"TranscriptSummarizer MAP-signals turn {turn} produced \"{parse.ToolName}\" instead of report_chunk_signals.");
        }
        else if (expectReduce && !parse.IsReduce)
        {
            Warn($"TranscriptSummarizer REDUCE turn {turn} produced \"{parse.ToolName}\" instead of reduce.");
        }

        return new TranscriptSummarizerTurnResult(turn, timestamp, parse, benchmark, responseJson);
    }

    private static string BuildPriorChunkRetellings(IReadOnlyList<TranscriptSummarizerMapOutput> priorSummaries)
    {
        if (priorSummaries.Count == 0)
        {
            return "  - none (this is the first chunk)";
        }

        var sb = new StringBuilder();
        for (int i = 0; i < priorSummaries.Count; i++)
        {
            if (i > 0) sb.AppendLine();
            sb.Append("  - [Chunk ").Append(i + 1).Append("] ").Append(priorSummaries[i].Retelling);
        }
        return sb.ToString();
    }

    private static string BuildChunkHeader(int chunkIndex, int totalChunks, bool signalsContext)
    {
        if (chunkIndex == 1)
        {
            return signalsContext
                ? $"Transcript chunk 1 of {totalChunks} — this is the START of the talk. Default `structure` to \"intro\" whenever introductory framing is present (greeting, naming the subject, situating the topic, opening anecdote):"
                : $"Transcript chunk 1 of {totalChunks}:";
        }
        return signalsContext
            ? $"Transcript chunk {chunkIndex} of {totalChunks} (use the prior chunk retellings above as context to judge what is load-bearing here):"
            : $"Transcript chunk {chunkIndex} of {totalChunks}:";
    }

    private static void ValidateChunkIndex(int chunkIndex, int totalChunks)
    {
        if (chunkIndex < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(chunkIndex), chunkIndex, "Chunk index is 1-based.");
        }
        if (totalChunks < chunkIndex)
        {
            throw new ArgumentOutOfRangeException(nameof(totalChunks), totalChunks, "Total chunks must cover the current index.");
        }
    }

    private static TranscriptSummarizerConversation Create(
        EngineHandle engine,
        string systemPrompt,
        bool enableThinking,
        string toolsJson,
        Action<string>? warn,
        string role)
    {
        Guard.NotNull(engine);

        ConversationConfigHandle? config = null;
        try
        {
            config = GemmaStageNative.ConversationConfigCreate(
                engine,
                systemPrompt,
                toolsJson,
                enableConstrainedDecoding: true);
            GemmaStageNative.ConversationConfigSetEnableThinking(config, enableThinking);
        }
        catch
        {
            config?.Dispose();
            throw;
        }

        return new TranscriptSummarizerConversation(engine, config, warn, role, enableThinking);
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
