using GemmaStage.Session.Native;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Transcript;
using System;
using System.Collections.Generic;
using System.Linq;

namespace GemmaStage.Session.Clarification;

// Orchestrates the Clarification phase:
//   1. Revise - deduplicate, lift type per merge severity rule
//      (topic_unknown > comprehension_gap > detail_request), and produce the
//      revised question list.
//   2. Resolve - walk the transcript in CT-token chunks, removing questions
//      that the chunk answers. Necessary because an early-overflow concern
//      may have been archived before its answer landed in the talk; Inquirer
//      could not have resolved it in real time. A fresh conversation is
//      created per chunk so each Resolve call reasons over a clean KV cache.
//
// Returns a ClarificationResult describing what was raised, what was resolved,
// and what remains unresolved (the Final Q&A set).
public sealed class ClarificationRunner
{
    private readonly EngineHandle _engine;
    private readonly int _tokenBudget;
    private readonly Action<string>? _warn;

    public ClarificationRunner(
        EngineHandle engine,
        int tokenBudget = 1000,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 1000;
        _warn = warn;
    }

    public ClarificationResult Run(
        ConcernArchive concernArchive,
        TranscriptStore transcriptStore,
        MetricsStore metricsStore)
    {
        Guard.NotNull(concernArchive);
        Guard.NotNull(transcriptStore);
        Guard.NotNull(metricsStore);

        var archived = DistinctById(concernArchive.Snapshot());
        if (archived.Count == 0)
        {
            Warn("Clarification: concern archive is empty; nothing to process.");
            return ClarificationResult.Skipped;
        }

        // Step 1 - Revise
        ClarificationTurnResult reviseTurn;
        IReadOnlyList<RevisedQuestion> revised;
        using (var conv = new ClarificationConversation(_engine, _warn))
        {
            reviseTurn = conv.Revise(archived);
        }

        if (!reviseTurn.Parse.IsRevise || reviseTurn.Parse.Revise is null)
        {
            Warn("Clarification: Revise step failed or produced no questions.");
            return new ClarificationResult(
                Ran: true,
                OriginalArchivedCount: archived.Count,
                RevisedQuestions: Array.Empty<RevisedQuestion>(),
                ResolvedIds: Array.Empty<long>(),
                UnresolvedQuestions: Array.Empty<RevisedQuestion>(),
                ReviseTurn: reviseTurn,
                ResolveTurns: Array.Empty<ClarificationTurnResult>());
        }

        revised = reviseTurn.Parse.Revise.Questions;
        if (revised.Count == 0)
        {
            return new ClarificationResult(
                Ran: true,
                OriginalArchivedCount: archived.Count,
                RevisedQuestions: revised,
                ResolvedIds: Array.Empty<long>(),
                UnresolvedQuestions: Array.Empty<RevisedQuestion>(),
                ReviseTurn: reviseTurn,
                ResolveTurns: Array.Empty<ClarificationTurnResult>());
        }

        // Step 2 - Resolve per transcript chunk
        var chunks = BuildChunks(transcriptStore, metricsStore);
        var remaining = revised.ToDictionary(c => c.Id, c => c);
        var resolvedIds = new HashSet<long>();
        var resolveTurns = new List<ClarificationTurnResult>(chunks.Count);

        foreach (var chunk in chunks)
        {
            if (remaining.Count == 0)
            {
                break;
            }

            ClarificationTurnResult resolveTurn;
            using (var resolveConv = new ClarificationConversation(_engine, _warn))
            {
                resolveTurn = resolveConv.Resolve(chunk, remaining.Values.ToList());
            }

            resolveTurns.Add(resolveTurn);

            if (!resolveTurn.Parse.IsResolve || resolveTurn.Parse.Resolve is null)
            {
                continue;
            }

            foreach (var id in resolveTurn.Parse.Resolve.ResolvedIds)
            {
                if (remaining.Remove(id))
                {
                    resolvedIds.Add(id);
                }
            }
        }

        return new ClarificationResult(
            Ran: true,
            OriginalArchivedCount: archived.Count,
            RevisedQuestions: revised,
            ResolvedIds: resolvedIds.ToArray(),
            UnresolvedQuestions: remaining.Values.ToArray(),
            ReviseTurn: reviseTurn,
            ResolveTurns: resolveTurns);
    }

    private IReadOnlyList<string> BuildChunks(
        TranscriptStore transcriptStore,
        MetricsStore metricsStore)
    {
        var transcripts = transcriptStore.Snapshot();
        var metrics = metricsStore.Snapshot();

        var chunkCompletedBySequence = new Dictionary<long, bool>(metrics.Count);
        foreach (var metric in metrics)
        {
            chunkCompletedBySequence[metric.Sequence] = metric.ChunkCompleted;
        }

        var builder = new TranscriptChunkBuilder(_tokenBudget);
        var chunks = new List<string>();

        foreach (var transcript in transcripts)
        {
            chunkCompletedBySequence.TryGetValue(transcript.Sequence, out var completed);
            var chunk = builder.Feed(transcript.Text, completed);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }

        var flushed = builder.Flush();
        if (flushed is not null)
        {
            chunks.Add(flushed);
        }

        return chunks;
    }

    // The archive can contain the same id from overflow and session-end paths.
    // Collapse on id so Revise receives each concern exactly once.
    private static IReadOnlyList<ArchivedConcernEntry> DistinctById(
        IReadOnlyList<ArchivedConcernEntry> entries)
    {
        var seen = new HashSet<long>();
        var result = new List<ArchivedConcernEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (seen.Add(entry.Id))
            {
                result.Add(entry);
            }
        }

        return result;
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}
