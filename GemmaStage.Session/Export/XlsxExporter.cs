using ClosedXML.Excel;
using GemmaStage.Session.Clarification;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.GroundTruthSummarizer;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Transcript;
using GemmaStage.Session.TranscriptSummarizer;

namespace GemmaStage.Session.Export;

// Exports all session data into a multi-sheet Excel workbook.
// Each module gets its own sheet; every row is one history item.
//
// Moved from the PoC's standalone exe into the session module so the Unity
// SessionLayer can produce the same artifact at session-end. PoC parity:
// passes the live Session + the pipeline result records this exporter reads.
public static class XlsxExporter
{
    private static readonly XLColor InputRowColor = XLColor.FromHtml("#E5F4F7");

    public static void Export(
        string path,
        Session session,
        TranscriptSummarizerResult? summarizerResult,
        ClarificationResult? clarificationResult,
        DeepDiveResult? deepDiveResult,
        MainIdeaComparatorResult? mainIdeaComparator = null,
        GroundTruthSummarizerResult? groundTruthResult = null,
        IReadOnlyList<MetricsLogEntry>? memSamples = null,
        IReadOnlyList<RoundRecord>? rounds = null)
    {
        using var workbook = new XLWorkbook();

        WritePerceptorSheet(workbook, session);
        WriteIdeaReflectorSheet(workbook, session);
        WriteInquirerSheet(workbook, session);
        WriteConcernArchiveSheet(workbook, session);
        WriteQaRoundsSheet(workbook, session, rounds ?? Array.Empty<RoundRecord>());
        WriteClarificationSheet(workbook, session, clarificationResult);
        WriteTranscriptSummarizerSheet(workbook, session, summarizerResult);
        WriteGroundTruthSummarizerSheet(workbook, session, groundTruthResult);
        WriteMainIdeaComparatorSheet(workbook, mainIdeaComparator);
        WriteDeepDiveSheet(workbook, session, summarizerResult, clarificationResult, mainIdeaComparator, deepDiveResult);
        WriteMemSheet(workbook, memSamples ?? Array.Empty<MetricsLogEntry>());

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        workbook.SaveAs(path);
    }

    private static void WritePerceptorSheet(XLWorkbook workbook, Session session)
    {
        var ws = workbook.Worksheets.Add("Perceptor");

        // Headers
        ws.Cell(1, 1).Value = "#";
        ws.Cell(1, 2).Value = "Timestamp";
        ws.Cell(1, 3).Value = "Transcript";
        ws.Cell(1, 4).Value = "Clarity";
        ws.Cell(1, 5).Value = "Emotion";
        ws.Cell(1, 6).Value = "Grammar";
        ws.Cell(1, 7).Value = "ChunkCompleted";
        ws.Cell(1, 8).Value = "TTFT (s)";
        ws.Cell(1, 9).Value = "Decode tok/s";
        ws.Cell(1, 10).Value = "Decode Tokens";
        StyleHeaderRow(ws, 10);

        var transcripts = session.TranscriptStore.Snapshot();
        var metrics = session.MetricsStore.Snapshot();
        var metricsBySeq = new Dictionary<long, MetricEntry>();
        foreach (var m in metrics) metricsBySeq[m.Sequence] = m;

        var row = 2;
        for (int i = 0; i < transcripts.Count; i++)
        {
            var t = transcripts[i];
            var inputRow = row++;

            ws.Cell(inputRow, 1).Value = t.Sequence;
            ws.Cell(inputRow, 2).Value = FormatClockTime(t.Timestamp);
            ws.Cell(inputRow, 3).Value = "[INPUT] audio chunk";
            StyleInputRow(ws, inputRow, 10);

            var outputRow = row++;
            ws.Cell(outputRow, 1).Value = t.Sequence;
            ws.Cell(outputRow, 2).Value = FormatClockTime(t.Timestamp);
            ws.Cell(outputRow, 3).Value = t.Text;

            if (metricsBySeq.TryGetValue(t.Sequence, out var metric))
            {
                ws.Cell(outputRow, 4).Value = metric.Clarity ?? "";
                ws.Cell(outputRow, 5).Value = metric.Emotion ?? "";
                ws.Cell(outputRow, 6).Value = metric.Grammar ?? "";
                ws.Cell(outputRow, 7).Value = metric.ChunkCompleted;

                if (metric.TimeToFirstTokenSeconds.HasValue)
                    ws.Cell(outputRow, 8).Value = metric.TimeToFirstTokenSeconds.Value;
                if (metric.DecodeTokensPerSecond.HasValue)
                    ws.Cell(outputRow, 9).Value = metric.DecodeTokensPerSecond.Value;
                if (metric.DecodeTokenCount.HasValue)
                    ws.Cell(outputRow, 10).Value = metric.DecodeTokenCount.Value;
            }
        }

        // Also add image examinations if any.
        var images = session.ImageStore.Snapshot();
        if (images.Count > 0)
        {
            var wsImg = workbook.Worksheets.Add("Perceptor — Images");
            wsImg.Cell(1, 1).Value = "#";
            wsImg.Cell(1, 2).Value = "Timestamp";
            wsImg.Cell(1, 3).Value = "Examination";
            StyleHeaderRow(wsImg, 3);

            var imageRow = 2;
            for (int i = 0; i < images.Count; i++)
            {
                var img = images[i];
                var inputRow = imageRow++;
                wsImg.Cell(inputRow, 1).Value = img.Sequence;
                wsImg.Cell(inputRow, 2).Value = FormatClockTime(img.Timestamp);
                wsImg.Cell(inputRow, 3).Value = "[INPUT] image payload";
                StyleInputRow(wsImg, inputRow, 3);

                var outputRow = imageRow++;
                wsImg.Cell(outputRow, 1).Value = img.Sequence;
                wsImg.Cell(outputRow, 2).Value = FormatClockTime(img.Timestamp);
                wsImg.Cell(outputRow, 3).Value = img.Examination;
            }
        }
    }

    private static void WriteIdeaReflectorSheet(XLWorkbook workbook, Session session)
    {
        var ws = workbook.Worksheets.Add("IdeaReflector");

        ws.Cell(1, 1).Value = "Cycle #";
        ws.Cell(1, 2).Value = "Timestamp";
        ws.Cell(1, 3).Value = "Retelling";
        ws.Cell(1, 4).Value = "Topic";
        ws.Cell(1, 5).Value = "Domain Updates";
        ws.Cell(1, 6).Value = "Domain New";
        ws.Cell(1, 7).Value = "Audience-side recall (Topic + domain claims)";
        StyleHeaderRow(ws, 7);

        var reflections = session.RetellingsHistory.Snapshot();
        var row = 2;
        for (int i = 0; i < reflections.Count; i++)
        {
            var r = reflections[i];
            var inputRow = row++;

            ws.Cell(inputRow, 1).Value = r.CycleIndex;
            ws.Cell(inputRow, 2).Value = FormatClockTime(r.Timestamp);
            ws.Cell(inputRow, 3).Value = FormatRawContent(r.RawContent);
            StyleInputRow(ws, inputRow, 7);

            var outputRow = row++;
            ws.Cell(outputRow, 1).Value = r.CycleIndex;
            ws.Cell(outputRow, 2).Value = FormatClockTime(r.Timestamp);
            ws.Cell(outputRow, 3).Value = r.Retelling;
            ws.Cell(outputRow, 4).Value = r.IdeaReflectorOutput?.Topic ?? "";
            ws.Cell(outputRow, 5).Value = r.IdeaReflectorOutput?.MainIdeaUnderstanding ?? "";
            ws.Cell(outputRow, 6).Value = "";
            ws.Cell(outputRow, 7).Value = StripThesisLine(r.StateAfter.MainIdeaUnderstanding);
        }
    }

    // The rendered audience-side recall on InquirerStateSnapshot includes a
    // "Thesis:" line that IdeaReflector itself never produces — the thesis is
    // inferred post-performance by AudienceThesisInference. The IdeaReflector
    // sheet shows only what IdeaReflector owns: topic + per-domain claims.
    private static string StripThesisLine(string? rendered)
    {
        if (string.IsNullOrEmpty(rendered)) return string.Empty;
        var lines = rendered.Replace("\r\n", "\n").Split('\n');
        var sb = new System.Text.StringBuilder();
        foreach (var line in lines)
        {
            if (line.StartsWith("Thesis:", StringComparison.Ordinal)) continue;
            if (sb.Length > 0) sb.AppendLine();
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static void WriteInquirerSheet(XLWorkbook workbook, Session session)
    {
        var ws = workbook.Worksheets.Add("Inquirer");

        ws.Cell(1, 1).Value = "Cycle #";
        ws.Cell(1, 2).Value = "Timestamp";
        ws.Cell(1, 3).Value = "Raw Content";
        ws.Cell(1, 4).Value = "Confusion";
        ws.Cell(1, 5).Value = "New Concerns";
        ws.Cell(1, 6).Value = "Removed Concerns";
        ws.Cell(1, 7).Value = "Open Concerns After";
        StyleHeaderRow(ws, 7);

        var reflections = session.RetellingsHistory.Snapshot();
        var row = 2;
        for (int i = 0; i < reflections.Count; i++)
        {
            var r = reflections[i];
            var inputRow = row++;

            ws.Cell(inputRow, 1).Value = r.CycleIndex;
            ws.Cell(inputRow, 2).Value = FormatClockTime(r.Timestamp);
            ws.Cell(inputRow, 3).Value = FormatRawContent(r.RawContent);
            StyleInputRow(ws, inputRow, 7);

            var outputRow = row++;
            ws.Cell(outputRow, 1).Value = r.CycleIndex;
            ws.Cell(outputRow, 2).Value = FormatClockTime(r.Timestamp);
            ws.Cell(outputRow, 4).Value = (r.StateAfter.ConfusionScore ?? InquirerConfusionScore.Low).ToString();

            if (r.Reflection.NewConcerns.Count > 0)
            {
                ws.Cell(outputRow, 5).Value = string.Join("; ",
                    r.Reflection.NewConcerns.Select(c => $"[{c.Id}] ({c.Type}) {c.Question}"));
            }

            if (r.Reflection.RemovedConcerns.Count > 0)
            {
                ws.Cell(outputRow, 6).Value = string.Join("; ",
                    r.Reflection.RemovedConcerns.Select(c => $"[{c.Id}] {c.Cause}: {c.Note}"));
            }

            ws.Cell(outputRow, 7).Value = r.StateAfter.Concerns.Count;
        }
    }

    private static string FormatClaimList(IReadOnlyList<string>? claims)
    {
        if (claims is null || claims.Count == 0)
        {
            return string.Empty;
        }
        return string.Join("\n", claims);
    }

    private static string FormatRawContent(IReadOnlyList<RawContentSegment> segments)
    {
        if (segments.Count == 0)
        {
            return "[INPUT] (no new content)";
        }

        var lines = segments.Select(static s =>
        {
            var label = s.Kind == RawContentKind.Image ? "Slide" : "Audio";
            return $"[{label}] {s.Text}";
        });
        return "[INPUT]\n" + string.Join("\n", lines);
    }

    private static void WriteConcernArchiveSheet(XLWorkbook workbook, Session session)
    {
        var ws = workbook.Worksheets.Add("Concern Archive");

        ws.Cell(1, 1).Value = "ID";
        ws.Cell(1, 2).Value = "Type";
        ws.Cell(1, 3).Value = "Question";
        ws.Cell(1, 4).Value = "Archived At";
        ws.Cell(1, 5).Value = "Reason";
        StyleHeaderRow(ws, 5);

        var archived = session.ConcernArchive.Snapshot();
        for (int i = 0; i < archived.Count; i++)
        {
            var c = archived[i];
            int row = i + 2;

            ws.Cell(row, 1).Value = c.Id;
            ws.Cell(row, 2).Value = c.Type.ToString();
            ws.Cell(row, 3).Value = c.Question;
            ws.Cell(row, 4).Value = FormatClockTime(c.ArchivedAt);
            ws.Cell(row, 5).Value = c.Reason;
        }
    }

    private static void WriteQaRoundsSheet(XLWorkbook workbook, Session session, IReadOnlyList<RoundRecord> rounds)
    {
        var ws = workbook.Worksheets.Add("Q&A Rounds");

        ws.Cell(1, 1).Value = "#";
        ws.Cell(1, 2).Value = "Phase";
        ws.Cell(1, 3).Value = "Concern ID";
        ws.Cell(1, 4).Value = "Type";
        ws.Cell(1, 5).Value = "Question";
        ws.Cell(1, 6).Value = "Speaker Answer (injected as transcript)";
        ws.Cell(1, 7).Value = "Opened";
        ws.Cell(1, 8).Value = "Closed";
        ws.Cell(1, 9).Value = "Duration (s)";
        ws.Cell(1, 10).Value = "Sliced Answer (DeepDive Q&A span)";
        ws.Cell(1, 11).Value = "Resolution (DeepDive)";
        StyleHeaderRow(ws, 11);

        var history = session.QaRoundsHistory.Snapshot();
        var byConcernAndOpened = new Dictionary<(long, DateTimeOffset), QaRoundEntry>();
        foreach (var entry in history)
        {
            byConcernAndOpened[(entry.ConcernId, entry.OpenedAt)] = entry;
        }

        // Re-derive what DeepDive's Q&A Handling sub-role would see: sliced
        // transcript text by timestamp window + resolution decision from
        // inquirer reflections + clarification.ResolvedIds. Mirrors
        // DeepDiveInputBuilder.BuildQaSpans (§13.4).
        var reflections = session.RetellingsHistory.Snapshot();
        var transcripts = session.TranscriptStore.Snapshot();

        var resolvedIds = new HashSet<long>();
        foreach (var r in reflections)
        {
            foreach (var removed in r.Reflection.RemovedConcerns)
            {
                if (removed.Cause == RemovedConcernCause.Resolved)
                {
                    resolvedIds.Add(removed.Id);
                }
            }
        }

        if (rounds.Count == 0 && history.Count == 0)
        {
            ws.Cell(2, 1).Value = "(no Q&A rounds occurred)";
            return;
        }

        int row = 2;
        for (int i = 0; i < rounds.Count; i++)
        {
            var rr = rounds[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = rr.Phase;
            ws.Cell(row, 3).Value = rr.ConcernId;
            ws.Cell(row, 4).Value = rr.ConcernType;
            ws.Cell(row, 5).Value = rr.Question;
            ws.Cell(row, 6).Value = rr.Answer;
            ws.Cell(row, 7).Value = FormatClockTime(rr.OpenedAt);
            ws.Cell(row, 8).Value = FormatClockTime(rr.ClosedAt);
            ws.Cell(row, 9).Value = (rr.ClosedAt - rr.OpenedAt).TotalSeconds;

            ws.Cell(row, 10).Value = SliceAnswer(transcripts, rr.OpenedAt, rr.ClosedAt);
            ws.Cell(row, 11).Value = resolvedIds.Contains(rr.ConcernId) ? "Resolved" : "NotAnswered";

            row++;
        }

        ws.Columns().AdjustToContents();
    }

    private static string SliceAnswer(IReadOnlyList<TranscriptEntry> transcripts, DateTimeOffset openedAt, DateTimeOffset closedAt)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var t in transcripts)
        {
            if (t.Timestamp < openedAt || t.Timestamp > closedAt) continue;
            if (string.IsNullOrWhiteSpace(t.Text)) continue;
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(t.Text);
        }
        return sb.ToString();
    }

    private static void WriteClarificationSheet(XLWorkbook workbook, Session session, ClarificationResult? result)
    {
        var ws = workbook.Worksheets.Add("Clarification");

        ws.Cell(1, 1).Value = "Stage";
        ws.Cell(1, 2).Value = "ID";
        ws.Cell(1, 3).Value = "Question";
        ws.Cell(1, 4).Value = "Status";
        StyleHeaderRow(ws, 4);

        if (result is null || !result.Ran)
        {
            ws.Cell(2, 1).Value = "(skipped)";
            return;
        }

        int row = 2;
        foreach (var concern in session.ConcernArchive.Snapshot())
        {
            ws.Cell(row, 1).Value = "INPUT";
            ws.Cell(row, 2).Value = concern.Id;
            ws.Cell(row, 3).Value = concern.Question;
            ws.Cell(row, 4).Value = concern.Reason;
            StyleInputRow(ws, row, 4);
            row++;
        }

        var resolvedIds = new HashSet<long>(result.ResolvedIds);
        foreach (var question in result.RevisedQuestions)
        {
            ws.Cell(row, 1).Value = "revised";
            ws.Cell(row, 2).Value = question.Id;
            ws.Cell(row, 3).Value = $"({question.Type}) {question.Question}";
            ws.Cell(row, 4).Value = resolvedIds.Contains(question.Id) ? "resolved" : "unresolved";
            row++;
        }
    }

    private static void WriteTranscriptSummarizerSheet(XLWorkbook workbook, Session session, TranscriptSummarizerResult? result)
    {
        var ws = workbook.Worksheets.Add("TranscriptSummarizer");

        ws.Cell(1, 1).Value = "Stage";
        ws.Cell(1, 2).Value = "Chunk #";
        ws.Cell(1, 3).Value = "Retelling / Inferred Main Idea";
        ws.Cell(1, 4).Value = "Structure";
        ws.Cell(1, 5).Value = "Consistency";
        ws.Cell(1, 6).Value = "Support";
        ws.Cell(1, 7).Value = "Notes";
        StyleHeaderRow(ws, 7);

        if (result is null) return;

        var inputChunks = BuildTranscriptChunks(session);
        int row = 2;
        for (int i = 0; i < result.MapOutputs.Count; i++)
        {
            var map = result.MapOutputs[i];
            if (inputChunks.Count > i)
            {
                ws.Cell(row, 1).Value = "MAP INPUT";
                ws.Cell(row, 2).Value = i + 1;
                ws.Cell(row, 3).Value = inputChunks[i];
                StyleInputRow(ws, row, 7);
                row++;
            }

            ws.Cell(row, 1).Value = "MAP OUTPUT";
            ws.Cell(row, 2).Value = i + 1;
            ws.Cell(row, 3).Value = map.Retelling;
            ws.Cell(row, 4).Value = map.Structure;
            ws.Cell(row, 5).Value = map.Consistency;
            ws.Cell(row, 6).Value = map.Support;
            if (map.Notes.Count > 0)
                ws.Cell(row, 7).Value = string.Join("; ", map.Notes);
            row++;
        }

        if (result.ReduceOutput is { } reduce)
        {
            ws.Cell(row, 1).Value = "REDUCE INPUT";
            ws.Cell(row, 3).Value = BuildReduceInput(result.MapOutputs);
            StyleInputRow(ws, row, 7);
            row++;

            ws.Cell(row, 1).Value = "REDUCE OUTPUT";
            ws.Cell(row, 3).Value = reduce.InferredMainIdeaFromTranscript;
        }
    }

    private static void WriteGroundTruthSummarizerSheet(
        XLWorkbook workbook,
        Session session,
        GroundTruthSummarizerResult? result)
    {
        var ws = workbook.Worksheets.Add("GroundTruthSummarizer");

        ws.Cell(1, 1).Value = "Stage";
        ws.Cell(1, 2).Value = "Chunk #";
        ws.Cell(1, 3).Value = "Content / Source / Prior Claims";
        ws.Cell(1, 4).Value = "MAP New Claims";
        ws.Cell(1, 5).Value = "Final Claims (REDUCE)";
        ws.Cell(1, 6).Value = "Main Thesis";
        StyleHeaderRow(ws, 6);

        if (result is null || !result.Ran || result.Output is null)
        {
            ws.Cell(2, 1).Value = "(skipped)";
            if (result?.SourcePath is not null)
            {
                ws.Cell(2, 3).Value = $"Source: {result.SourcePath} (kind={result.SourceKind})";
            }
            return;
        }

        int row = 2;

        ws.Cell(row, 1).Value = "SOURCE";
        ws.Cell(row, 3).Value = $"{result.SourcePath}  (kind={result.SourceKind})";
        StyleInputRow(ws, row, 6);
        row++;

        IReadOnlyList<string> chunks = ReBuildGtChunks(session, result);

        // Replay the running claim accumulator so each chunk's INPUT cell shows
        // the prior-claims block the runner actually fed in.
        var accumulator = new List<string>();

        for (int i = 0; i < result.MapTurns.Count; i++)
        {
            var turn = result.MapTurns[i];
            var chunkNumber = i + 1;

            ws.Cell(row, 1).Value = "MAP INPUT";
            ws.Cell(row, 2).Value = chunkNumber;
            var inputBlock = new System.Text.StringBuilder();
            if (i < chunks.Count)
            {
                inputBlock.AppendLine("--- chunk text ---");
                inputBlock.AppendLine(chunks[i]);
                inputBlock.AppendLine();
            }
            inputBlock.AppendLine("--- prior claims ---");
            inputBlock.Append(accumulator.Count == 0
                ? "  (none yet)"
                : string.Join("\n", accumulator.Select(c => "  - " + c)));
            ws.Cell(row, 3).Value = inputBlock.ToString().TrimEnd();
            StyleInputRow(ws, row, 6);
            row++;

            ws.Cell(row, 1).Value = "MAP OUTPUT";
            ws.Cell(row, 2).Value = chunkNumber;
            if (turn.Parse.Map is { } mapOut)
            {
                ws.Cell(row, 4).Value = FormatClaimList(mapOut.Claims);
                foreach (var claim in mapOut.Claims)
                {
                    var trimmed = claim.Trim();
                    if (!string.IsNullOrEmpty(trimmed)) accumulator.Add(trimmed);
                }
            }
            else
            {
                ws.Cell(row, 3).Value = $"(MAP failed: {turn.Parse.Error ?? "unknown"})";
            }
            row++;
        }

        ws.Cell(row, 1).Value = "REDUCE INPUT";
        ws.Cell(row, 3).Value = accumulator.Count == 0
            ? "(none)"
            : string.Join("\n", accumulator.Select(c => "  - " + c));
        StyleInputRow(ws, row, 6);
        row++;

        ws.Cell(row, 1).Value = "REDUCE OUTPUT";
        ws.Cell(row, 5).Value = FormatClaimList(result.Output.Claims);
        ws.Cell(row, 6).Value = result.Output.MainThesis;
    }

    private static IReadOnlyList<string> ReBuildGtChunks(Session session, GroundTruthSummarizerResult result)
    {
        if (result.SourcePath is null) return Array.Empty<string>();

        try
        {
            string? text = result.SourceKind switch
            {
                GroundTruthSourceKind.Text when File.Exists(result.SourcePath) => File.ReadAllText(result.SourcePath),
                GroundTruthSourceKind.Image => result.ImageTurn?.Parse.Image?.Examination,
                _ => null,
            };

            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
            return GroundTruthSummarizerRunner.BuildChunks(text, session.Config.TranscriptSummarizerTokenBudget);
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static void WriteMainIdeaComparatorSheet(XLWorkbook workbook, MainIdeaComparatorResult? result)
    {
        var ws = workbook.Worksheets.Add("MainIdeaComparator");

        if (result?.Output is not { } cmp)
        {
            ws.Cell(1, 1).Value = "(skipped — no ground-truth file attached or GT summarizer failed)";
            return;
        }

        ws.Cell(1, 1).Value = "Recall";
        ws.Cell(1, 2).Value = cmp.Recall.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        ws.Cell(2, 1).Value = "Anchor claims";
        ws.Cell(2, 2).Value = cmp.AnchorClaims.Count;
        ws.Cell(1, 1).Style.Font.Bold = true;
        ws.Cell(2, 1).Style.Font.Bold = true;

        ws.Cell(4, 1).Value = "Anchor thesis";
        ws.Cell(4, 1).Style.Font.Bold = true;
        ws.Cell(4, 2).Value = cmp.AnchorThesis;

        ws.Cell(5, 1).Value = "Audience thesis";
        ws.Cell(5, 1).Style.Font.Bold = true;
        ws.Cell(5, 2).Value = cmp.AudienceThesis;

        ws.Cell(6, 1).Value = "Thesis comparison";
        ws.Cell(6, 1).Style.Font.Bold = true;
        ws.Cell(6, 2).Value = cmp.ThesisComparison;

        ws.Cell(7, 1).Value = "Audience main_idea_understanding";
        ws.Cell(7, 1).Style.Font.Bold = true;
        ws.Cell(7, 2).Value = cmp.AudienceMainIdeaUnderstanding;

        // Per-claim coverage table
        int row = 9;
        ws.Cell(row, 1).Value = "#";
        ws.Cell(row, 2).Value = "Coverage";
        ws.Cell(row, 3).Value = "Anchor claim";
        ws.Cell(row, 4).Value = "Evidence";
        StyleHeaderRow(ws, 4, row);
        row++;

        for (int i = 0; i < cmp.ClaimCoverages.Count; i++)
        {
            var c = cmp.ClaimCoverages[i];
            ws.Cell(row, 1).Value = i + 1;
            ws.Cell(row, 2).Value = c.Coverage.ToString().ToLowerInvariant();
            ws.Cell(row, 3).Value = c.AnchorClaim;
            ws.Cell(row, 4).Value = c.Evidence ?? "";

            var coverageColor = c.Coverage switch
            {
                CoverageLabel.Yes => XLColor.FromHtml("#D6F0D6"),
                CoverageLabel.Partial => XLColor.FromHtml("#FFF3CD"),
                CoverageLabel.No => XLColor.FromHtml("#F8D7DA"),
                _ => XLColor.NoColor,
            };
            if (coverageColor != XLColor.NoColor)
            {
                ws.Range(row, 1, row, 4).Style.Fill.BackgroundColor = coverageColor;
            }
            row++;
        }
    }

    private static void WriteDeepDiveSheet(
        XLWorkbook workbook,
        Session session,
        TranscriptSummarizerResult? summarizerResult,
        ClarificationResult? clarificationResult,
        MainIdeaComparatorResult? mainIdeaComparator,
        DeepDiveResult? result)
    {
        // Build per-sub-role inputs once; used for both the summary and role sheets.
        DeepDiveSubInputs? inputs = null;
        if (summarizerResult?.ReduceOutput is not null)
        {
            var cognitiveState = session.SnapshotCognitiveState();
            var finalMainIdea = cognitiveState.MainIdeaUnderstanding
                ?? "[IdeaReflector never converged on a main idea]";

            inputs = DeepDiveInputBuilder.Build(
                summarizerResult.ReduceOutput,
                finalMainIdea,
                summarizerResult.MapOutputs,
                session.RetellingsHistory,
                session.MetricsStore,
                session.TranscriptStore,
                session.QaRoundsHistory,
                clarificationResult,
                mainIdeaComparator?.Output);
        }

        WriteDeepDiveSummarySheet(workbook, result);

        if (result is null) return;

        var roles = new (string SheetName, DeepDiveCriterion Criterion, string? InputText)[]
        {
            ("DD — Main Idea", result.MainIdeaClarity,
                inputs is not null ? GemmaStage.Session.DeepDive.MainIdeaClarity.MainIdeaClarityInputFormatter.Format(inputs.MainIdeaClarity) : null),
            ("DD — Structure", result.Structure,
                inputs is not null ? GemmaStage.Session.DeepDive.Structure.StructureInputFormatter.Format(inputs.Structure) : null),
            ("DD — Consistency", result.ConsistencyFocus,
                inputs is not null ? GemmaStage.Session.DeepDive.ConsistencyFocus.ConsistencyFocusInputFormatter.Format(inputs.ConsistencyFocus) : null),
            ("DD — Support", result.SupportJustification,
                inputs is not null ? GemmaStage.Session.DeepDive.SupportJustification.SupportJustificationInputFormatter.Format(inputs.SupportJustification) : null),
            ("DD — Language", result.LanguageQuality,
                inputs is not null ? GemmaStage.Session.DeepDive.LanguageQuality.LanguageQualityInputFormatter.Format(inputs.LanguageQuality) : null),
            ("DD — Emotional", result.EmotionalDelivery,
                inputs is not null ? GemmaStage.Session.DeepDive.EmotionalDelivery.EmotionalDeliveryInputFormatter.Format(inputs.EmotionalDelivery) : null),
            ("DD — Q&A", result.QaHandling,
                inputs is not null ? GemmaStage.Session.DeepDive.QaHandling.QaHandlingInputFormatter.Format(inputs.QaHandling) : null),
        };

        foreach (var (sheetName, criterion, inputText) in roles)
        {
            WriteDeepDiveRoleSheet(workbook, sheetName, criterion, inputText);
        }
    }

    private static void WriteDeepDiveSummarySheet(XLWorkbook workbook, DeepDiveResult? result)
    {
        var ws = workbook.Worksheets.Add("DeepDive");

        ws.Cell(1, 1).Value = "Criterion";
        ws.Cell(1, 2).Value = "Score";
        ws.Cell(1, 3).Value = "Verdict";
        StyleHeaderRow(ws, 3);

        if (result is null)
        {
            ws.Cell(2, 1).Value = "(not run)";
            return;
        }

        var criteria = new (string Name, DeepDiveCriterion Criterion)[]
        {
            ("Main Idea Clarity", result.MainIdeaClarity),
            ("Structure", result.Structure),
            ("Consistency & Focus", result.ConsistencyFocus),
            ("Support & Justification", result.SupportJustification),
            ("Language Quality", result.LanguageQuality),
            ("Emotional Delivery", result.EmotionalDelivery),
            ("Q&A Handling", result.QaHandling),
        };

        for (int i = 0; i < criteria.Length; i++)
        {
            int row = i + 2;
            ws.Cell(row, 1).Value = criteria[i].Name;
            ws.Cell(row, 2).Value = criteria[i].Criterion.Value == DeepDiveScore.NotApplicable
                ? "N/A"
                : ((int)criteria[i].Criterion.Value).ToString();
            ws.Cell(row, 3).Value = criteria[i].Criterion.Verdict;
        }
    }

    private static void WriteDeepDiveRoleSheet(
        XLWorkbook workbook,
        string sheetName,
        DeepDiveCriterion criterion,
        string? inputText)
    {
        var ws = workbook.Worksheets.Add(sheetName);

        ws.Cell(1, 1).Value = "Stage";
        ws.Cell(1, 2).Value = "Score";
        ws.Cell(1, 3).Value = "Content";
        StyleHeaderRow(ws, 3);

        int row = 2;

        if (inputText is not null)
        {
            ws.Cell(row, 1).Value = "INPUT";
            ws.Cell(row, 3).Value = inputText.TrimEnd();
            StyleInputRow(ws, row, 3);
            row++;
        }

        ws.Cell(row, 1).Value = "OUTPUT";
        ws.Cell(row, 2).Value = criterion.Value == DeepDiveScore.NotApplicable
            ? "N/A"
            : ((int)criterion.Value).ToString();
        ws.Cell(row, 3).Value = criterion.Verdict;
    }

    private static void WriteMemSheet(XLWorkbook workbook, IReadOnlyList<MetricsLogEntry> samples)
    {
        var ws = workbook.Worksheets.Add("Mem");

        ws.Cell(1, 1).Value = "Timestamp";
        ws.Cell(1, 2).Value = "Stage";
        ws.Cell(1, 3).Value = "Backend";
        ws.Cell(1, 4).Value = "Tokens/s";
        ws.Cell(1, 5).Value = "KV Tokens";
        ws.Cell(1, 6).Value = "Context Limit";
        ws.Cell(1, 7).Value = "KV Cache %";
        ws.Cell(1, 8).Value = "VRAM MB";
        ws.Cell(1, 9).Value = "VRAM Bytes";
        ws.Cell(1, 10).Value = "VRAM Source";
        ws.Cell(1, 11).Value = "RSS MB";
        ws.Cell(1, 12).Value = "RSS Bytes";
        StyleHeaderRow(ws, 12);

        for (int i = 0; i < samples.Count; i++)
        {
            var sample = samples[i];
            int row = i + 2;

            ws.Cell(row, 1).Value = FormatElapsedTime(sample.TsMs);
            ws.Cell(row, 2).Value = sample.Stage;
            ws.Cell(row, 3).Value = sample.ActiveBackend ?? "";
            if (sample.TokensPerSec.HasValue)
                ws.Cell(row, 4).Value = sample.TokensPerSec.Value;
            if (sample.KvTokens.HasValue)
                ws.Cell(row, 5).Value = sample.KvTokens.Value;
            if (sample.ContextLimit.HasValue)
                ws.Cell(row, 6).Value = sample.ContextLimit.Value;
            if (sample.KvCachePercent.HasValue)
                ws.Cell(row, 7).Value = sample.KvCachePercent.Value;
            if (sample.VramBytes.HasValue)
            {
                ws.Cell(row, 8).Value = BytesToMiB(sample.VramBytes.Value);
                ws.Cell(row, 9).Value = Convert.ToDouble(sample.VramBytes.Value);
            }
            ws.Cell(row, 10).Value = sample.VramSource ?? "";
            if (sample.RssBytes.HasValue)
            {
                ws.Cell(row, 11).Value = BytesToMiB(sample.RssBytes.Value);
                ws.Cell(row, 12).Value = Convert.ToDouble(sample.RssBytes.Value);
            }
        }

        ws.Columns().AdjustToContents();
    }

    private static void StyleHeaderRow(IXLWorksheet ws, int colCount)
    {
        for (int c = 1; c <= colCount; c++)
        {
            ws.Cell(1, c).Style.Font.Bold = true;
        }
    }

    private static void StyleHeaderRow(IXLWorksheet ws, int colCount, int row)
    {
        for (int c = 1; c <= colCount; c++)
        {
            ws.Cell(row, c).Style.Font.Bold = true;
        }
    }

    private static void StyleInputRow(IXLWorksheet ws, int row, int colCount)
    {
        var range = ws.Range(row, 1, row, colCount);
        range.Style.Fill.BackgroundColor = InputRowColor;
        range.Style.Font.Italic = true;
    }

    private static IReadOnlyList<string> BuildTranscriptChunks(Session session)
    {
        var transcripts = session.TranscriptStore.Snapshot();
        var metrics = session.MetricsStore.Snapshot();
        var chunkCompletedBySequence = new Dictionary<long, bool>(metrics.Count);
        foreach (var metric in metrics)
        {
            chunkCompletedBySequence[metric.Sequence] = metric.ChunkCompleted;
        }

        var builder = new TranscriptChunkBuilder(session.Config.TranscriptSummarizerTokenBudget);
        var chunks = new List<string>();
        foreach (var transcript in transcripts)
        {
            chunkCompletedBySequence.TryGetValue(transcript.Sequence, out var chunkCompleted);
            var chunk = builder.Feed(transcript.Text, chunkCompleted);
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

    private static string BuildReduceInput(IReadOnlyList<TranscriptSummarizerMapOutput> mapOutputs)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < mapOutputs.Count; i++)
        {
            sb.Append("Chunk ").Append(i + 1).Append(": ").AppendLine(mapOutputs[i].Retelling);
        }
        return sb.ToString().TrimEnd();
    }


    private static string FormatClockTime(DateTimeOffset timestamp)
    {
        return timestamp.ToLocalTime().ToString("HH:mm:ss.fff");
    }

    private static string FormatElapsedTime(double tsMs)
    {
        var elapsed = TimeSpan.FromMilliseconds(tsMs);
        return $"{(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}.{elapsed.Milliseconds:000}";
    }

    private static double BytesToMiB(ulong bytes)
    {
        return bytes / 1024.0 / 1024.0;
    }
}
