using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using GemmaStage.Session.Native;
using GemmaStage.Session.Perceptor;
using GemmaStage.Session.Transcript;

namespace GemmaStage.Session.GroundTruthSummarizer;

public sealed record GroundTruthSummarizerResult(
    bool Ran,
    string? SourcePath,
    GroundTruthSourceKind SourceKind,
    GroundTruthSummarizerOutput? Output,
    IReadOnlyList<GroundTruthSummarizerTurnResult> MapTurns,
    GroundTruthSummarizerTurnResult? ReduceTurn,
    PerceptorTurnResult? ImageTurn)
{
    public static GroundTruthSummarizerResult Skipped { get; } = new(
        Ran: false,
        SourcePath: null,
        SourceKind: GroundTruthSourceKind.Unsupported,
        Output: null,
        MapTurns: Array.Empty<GroundTruthSummarizerTurnResult>(),
        ReduceTurn: null,
        ImageTurn: null);
}

public sealed class GroundTruthSummarizerRunner
{
    private static readonly Regex SentenceSplitter =
        new(@"(?<=[.!?])\s+", RegexOptions.Compiled);

    private readonly EngineHandle _engine;
    private readonly int _tokenBudget;
    private readonly Action<string>? _warn;

    public GroundTruthSummarizerRunner(
        EngineHandle engine,
        int tokenBudget = 500,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _tokenBudget = tokenBudget > 0 ? tokenBudget : 500;
        _warn = warn;
    }

    public GroundTruthSummarizerResult Run(GroundTruthDocInput? input)
    {
        input ??= GroundTruthDocInput.None;

        if (input is GroundTruthDocInput.NoneCase)
            return GroundTruthSummarizerResult.Skipped;

        var loader = new GroundTruthSourceLoader(_engine, _warn);
        GroundTruthSourceLoadResult loaded;
        string? sourcePath;

        switch (input)
        {
            case GroundTruthDocInput.FilePathCase fp:
                sourcePath = fp.Path;
                loaded = loader.Load(fp.Path);
                break;
            case GroundTruthDocInput.RasterizedImageBytesCase rib:
                sourcePath = null;
                loaded = loader.LoadFromBytes(rib.Bytes);
                break;
            default:
                return GroundTruthSummarizerResult.Skipped;
        }

        if (string.IsNullOrWhiteSpace(loaded.Text))
        {
            return new GroundTruthSummarizerResult(
                Ran: false,
                SourcePath: sourcePath,
                SourceKind: loaded.Kind,
                Output: null,
                MapTurns: Array.Empty<GroundTruthSummarizerTurnResult>(),
                ReduceTurn: null,
                ImageTurn: loaded.ImageTurn);
        }

        var chunks = BuildChunks(loaded.Text!, _tokenBudget);
        if (chunks.Count == 0)
        {
            return new GroundTruthSummarizerResult(
                Ran: false,
                SourcePath: sourcePath,
                SourceKind: loaded.Kind,
                Output: null,
                MapTurns: Array.Empty<GroundTruthSummarizerTurnResult>(),
                ReduceTurn: null,
                ImageTurn: loaded.ImageTurn);
        }

        // Flat claim accumulator. Each chunk's MAP sees the running list as
        // context so it can avoid restating already-captured claims.
        var accumulator = new List<string>();
        var mapTurns = new List<GroundTruthSummarizerTurnResult>(chunks.Count);

        using (var map = GroundTruthSummarizerConversation.CreateMap(_engine, _warn))
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var chunkIndex = i + 1;
                var turn = map.SendChunk(chunks[i], chunkIndex, chunks.Count, accumulator.AsReadOnly());
                mapTurns.Add(turn);

                if (turn.Parse.IsMap && turn.Parse.Map is { } mapOutput)
                {
                    foreach (var claim in mapOutput.Claims)
                    {
                        var trimmed = claim.Trim();
                        if (!string.IsNullOrWhiteSpace(trimmed))
                        {
                            accumulator.Add(trimmed);
                        }
                    }
                }
                else
                {
                    Warn($"GroundTruthSummarizer MAP chunk {chunkIndex}/{chunks.Count} failed: {turn.Parse.Error ?? "(no error)"}");
                }
            }
        }

        if (accumulator.Count == 0)
        {
            return new GroundTruthSummarizerResult(
                Ran: false,
                SourcePath: sourcePath,
                SourceKind: loaded.Kind,
                Output: null,
                MapTurns: mapTurns,
                ReduceTurn: null,
                ImageTurn: loaded.ImageTurn);
        }

        GroundTruthSummarizerTurnResult reduceTurn;
        using (var reduce = GroundTruthSummarizerConversation.CreateReduce(_engine, _warn))
        {
            reduceTurn = reduce.SendReduce(accumulator);
        }

        var output = reduceTurn.Parse.IsReduce ? reduceTurn.Parse.Reduce : null;

        return new GroundTruthSummarizerResult(
            Ran: output is not null,
            SourcePath: sourcePath,
            SourceKind: loaded.Kind,
            Output: output,
            MapTurns: mapTurns,
            ReduceTurn: reduceTurn,
            ImageTurn: loaded.ImageTurn);
    }

    public static IReadOnlyList<string> BuildChunks(string text, int tokenBudget)
    {
        var chunks = new List<string>();
        var chunker = new TranscriptChunkBuilder(tokenBudget);

        foreach (var sentence in SplitSentences(text))
        {
            var chunk = chunker.Feed(sentence, chunkCompleted: true);
            if (chunk is not null)
            {
                chunks.Add(chunk);
            }
        }

        var tail = chunker.Flush();
        if (tail is not null)
        {
            chunks.Add(tail);
        }

        return chunks;
    }

    internal static IReadOnlyList<string> SplitSentences(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<string>();
        }

        var trimmed = text.Trim();
        var parts = SentenceSplitter.Split(trimmed);
        var list = new List<string>(parts.Length);
        foreach (var raw in parts)
        {
            var s = raw.Trim();
            if (!string.IsNullOrWhiteSpace(s))
            {
                list.Add(s);
            }
        }

        return list;
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}
