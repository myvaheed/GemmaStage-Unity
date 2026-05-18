using GemmaStage.Session.Native;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Transcript;
using System;
using System.Collections.Generic;
using System.Text;

namespace GemmaStage.Session.TranscriptSummarizer;

public sealed record TranscriptSummarizerResult(
    IReadOnlyList<TranscriptSummarizerMapOutput> MapOutputs,
    TranscriptSummarizerReduceOutput? ReduceOutput,
    IReadOnlyList<TranscriptSummarizerTurnResult> MapTurns,
    TranscriptSummarizerTurnResult? ReduceTurn);

public sealed record TranscriptSummarizerMapStageResult(
    IReadOnlyList<TranscriptSummarizerMapOutput> MapOutputs,
    IReadOnlyList<TranscriptSummarizerTurnResult> MapTurns);

public sealed record TranscriptSummarizerReduceStageResult(
    IReadOnlyList<TranscriptSummarizerMapOutput> MapOutputs,
    TranscriptSummarizerReduceOutput? ReduceOutput,
    TranscriptSummarizerTurnResult? ReduceTurn);

public sealed class TranscriptSummarizerRunner
{
    private readonly EngineHandle _engine;
    private readonly int _tokenBudget;
    private readonly Action<string>? _warn;

    public TranscriptSummarizerRunner(
        EngineHandle engine,
        int tokenBudget = 1000,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 1000;
        _warn = warn;
    }

    public TranscriptSummarizerResult Run(
        TranscriptStore transcriptStore,
        MetricsStore metricsStore,
        Action<TranscriptSummarizerMapStageResult>? onMapCompleted = null,
        Action<TranscriptSummarizerReduceStageResult>? onReduceCompleted = null,
        QaRoundsHistory? qaRoundsHistory = null)
    {
        Guard.NotNull(transcriptStore);
        Guard.NotNull(metricsStore);

        var transcripts = transcriptStore.Snapshot();
        var chunks = BuildChunks(transcripts, metricsStore);
        var qaRounds = qaRoundsHistory?.Snapshot() ?? Array.Empty<QaRoundEntry>();

        if (chunks.Count == 0)
        {
            Warn("TranscriptSummarizer: no transcript chunks to process.");
            onMapCompleted?.Invoke(new TranscriptSummarizerMapStageResult(
                Array.Empty<TranscriptSummarizerMapOutput>(),
                Array.Empty<TranscriptSummarizerTurnResult>()));
            onReduceCompleted?.Invoke(new TranscriptSummarizerReduceStageResult(
                Array.Empty<TranscriptSummarizerMapOutput>(),
                null,
                null));
            return new TranscriptSummarizerResult(
                Array.Empty<TranscriptSummarizerMapOutput>(),
                null,
                Array.Empty<TranscriptSummarizerTurnResult>(),
                null);
        }

        var mapOutputs = new List<TranscriptSummarizerMapOutput>(chunks.Count);
        // mapTurns holds 2 entries per chunk (retelling + signals). Telemetry
        // surfaces both so failures on either pass are visible.
        var mapTurns = new List<TranscriptSummarizerTurnResult>(chunks.Count * 2);

        using (var retellingConv = TranscriptSummarizerConversation.CreateMapRetelling(_engine, _warn))
        using (var signalsConv = TranscriptSummarizerConversation.CreateMapSignals(_engine, _warn))
        {
            string? previousRetelling = null;
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkLabel = $"chunk {i + 1}/{chunks.Count}";
                TranscriptSummarizerTurnResult? retellingTurn = null;
                TranscriptSummarizerTurnResult? signalsTurn = null;

                var qaPairs = BuildQaPairsForChunk(chunks[i], qaRounds, transcripts);

                try
                {
                    retellingTurn = retellingConv.SendChunkForRetelling(
                        chunks[i].Text,
                        i + 1,
                        chunks.Count,
                        previousRetelling,
                        qaPairs);
                    mapTurns.Add(retellingTurn);
                }
                catch (Resilience.ToolCallFailureException ex)
                {
                    Warn($"TranscriptSummarizer MAP-retelling {chunkLabel} exhausted retries: {ex.Message}");
                }

                try
                {
                    signalsTurn = signalsConv.SendChunkForSignals(chunks[i].Text, priorSummaries: mapOutputs, i + 1, chunks.Count);
                    mapTurns.Add(signalsTurn);
                }
                catch (Resilience.ToolCallFailureException ex)
                {
                    Warn($"TranscriptSummarizer MAP-signals {chunkLabel} exhausted retries: {ex.Message}");
                }

                var retelling = retellingTurn?.Parse.MapRetelling;
                var signals = signalsTurn?.Parse.MapSignals;

                if (retelling is not null && signals is not null)
                {
                    mapOutputs.Add(new TranscriptSummarizerMapOutput(
                        retelling.Retelling,
                        signals.Structure,
                        signals.Consistency,
                        signals.Support,
                        signals.Notes));
                    previousRetelling = retelling.Retelling;
                }
                else
                {
                    Warn($"TranscriptSummarizer MAP {chunkLabel} did not produce a complete output (retelling={retelling is not null}, signals={signals is not null}).");
                    // Keep `previousRetelling` unchanged so the next chunk still
                    // has the last successful retelling to continue from.
                }
            }
        }

        onMapCompleted?.Invoke(new TranscriptSummarizerMapStageResult(
            mapOutputs.ToArray(),
            mapTurns.ToArray()));

        if (mapOutputs.Count == 0)
        {
            Warn("TranscriptSummarizer: all MAP stages failed; skipping REDUCE.");
            onReduceCompleted?.Invoke(new TranscriptSummarizerReduceStageResult(
                mapOutputs.ToArray(),
                null,
                null));
            return new TranscriptSummarizerResult(mapOutputs, null, mapTurns, null);
        }

        // REDUCE stage: one conversation over ordered MAP outputs.
        var reduceInput = BuildReduceInput(mapOutputs);
        TranscriptSummarizerReduceOutput? reduceOutput = null;
        TranscriptSummarizerTurnResult? reduceTurn = null;

        using (var reduceConversation = TranscriptSummarizerConversation.CreateReduce(_engine, _warn))
        {
            reduceTurn = reduceConversation.SendReducePrompt(reduceInput);
            if (reduceTurn.Parse.IsReduce && reduceTurn.Parse.Reduce is not null)
            {
                reduceOutput = reduceTurn.Parse.Reduce;
            }
            else
            {
                Warn("TranscriptSummarizer REDUCE stage did not produce a valid output.");
            }
        }

        onReduceCompleted?.Invoke(new TranscriptSummarizerReduceStageResult(
            mapOutputs.ToArray(),
            reduceOutput,
            reduceTurn));

        return new TranscriptSummarizerResult(mapOutputs, reduceOutput, mapTurns, reduceTurn);
    }

    private IReadOnlyList<TimedTranscriptChunk> BuildChunks(
        IReadOnlyList<TranscriptEntry> transcripts,
        MetricsStore metricsStore)
    {
        var metrics = metricsStore.Snapshot();

        // Build a lookup of chunk_completed flags by sequence number.
        var chunkCompletedBySequence = new Dictionary<long, bool>(metrics.Count);
        foreach (var metric in metrics)
        {
            chunkCompletedBySequence[metric.Sequence] = metric.ChunkCompleted;
        }

        var builder = new TranscriptChunkBuilder(_tokenBudget);
        var chunks = new List<TimedTranscriptChunk>();
        DateTimeOffset? chunkStart = null;
        DateTimeOffset chunkEnd = DateTimeOffset.MinValue;

        foreach (var transcript in transcripts)
        {
            chunkCompletedBySequence.TryGetValue(transcript.Sequence, out var chunkCompleted);

            chunkStart ??= transcript.Timestamp;
            chunkEnd = transcript.Timestamp;

            var emitted = builder.Feed(transcript.Text, chunkCompleted);
            if (emitted is not null)
            {
                chunks.Add(new TimedTranscriptChunk(emitted, chunkStart!.Value, chunkEnd));
                chunkStart = null;
            }
        }

        // Flush any remaining buffered text.
        var flushed = builder.Flush();
        if (flushed is not null)
        {
            var start = chunkStart ?? chunkEnd;
            chunks.Add(new TimedTranscriptChunk(flushed, start, chunkEnd));
        }

        return chunks;
    }

    // Returns the Q&A rounds whose lifetime overlaps the chunk's transcript
    // window, paired with the sliced answer transcript text for that round
    // (joined from transcript entries inside the round window). Used by
    // MAP retelling to render "Was asked... I answered..." blocks.
    private static IReadOnlyList<QaPromptPair> BuildQaPairsForChunk(
        TimedTranscriptChunk chunk,
        IReadOnlyList<QaRoundEntry> qaRounds,
        IReadOnlyList<TranscriptEntry> transcripts)
    {
        if (qaRounds.Count == 0) return Array.Empty<QaPromptPair>();

        List<QaPromptPair>? pairs = null;
        foreach (var round in qaRounds)
        {
            if (round.ClosedAt < chunk.Start || round.OpenedAt > chunk.End) continue;

            var answer = SliceAnswerText(transcripts, round.OpenedAt, round.ClosedAt);
            pairs ??= new List<QaPromptPair>();
            pairs.Add(new QaPromptPair(round.QuestionText, answer));
        }
        return (IReadOnlyList<QaPromptPair>?)pairs ?? Array.Empty<QaPromptPair>();
    }

    private static string SliceAnswerText(
        IReadOnlyList<TranscriptEntry> transcripts,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt)
    {
        var sb = new StringBuilder();
        foreach (var t in transcripts)
        {
            if (t.Timestamp < openedAt || t.Timestamp > closedAt) continue;
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    private static string BuildReduceInput(IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < mapOutputs.Count; i++)
        {
            sb.Append("Chunk ").Append(i + 1).Append(": ").AppendLine(mapOutputs[i].Retelling);
        }

        return sb.ToString();
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}

// Lightweight pair carried from the runner into the MAP retelling prompt.
public sealed record QaPromptPair(string Question, string Answer);
