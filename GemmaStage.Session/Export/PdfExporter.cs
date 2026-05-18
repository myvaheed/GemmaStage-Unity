using GemmaStage.Session.Clarification;
using GemmaStage.Session.DeepDive;
using GemmaStage.Session.Inquirer;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Stores;
using GemmaStage.Session.Transcript;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace GemmaStage.Session.Export;

// Player-facing PDF report. Produced at session end alongside the xlsx so the
// Results screen's "Save as PDF" button can open a finished file instantly.
//
// Sections, in order: cover header → Transcript (with timing) → Q&A Rounds →
// DeepDive (summary table + per-criterion verdicts) → Main Idea Analysis
// (only when a ground-truth doc was attached) → Clarification (only when it
// ran). The exporter never writes empty sections — missing inputs are
// skipped silently so a session with no Live/Final Q&A still produces a
// clean report.
public static class PdfExporter
{
    private static readonly Color HeaderBg = Color.FromHex("#1F3B5A");
    private static readonly Color HeaderText = Color.FromHex("#FFFFFF");
    private static readonly Color SectionAccent = Color.FromHex("#1F3B5A");
    private static readonly Color SubtleText = Color.FromHex("#5A6470");
    private static readonly Color TableHeaderBg = Color.FromHex("#E9EEF4");
    private static readonly Color ZebraBg = Color.FromHex("#F7F9FC");
    private static readonly Color DividerColor = Color.FromHex("#D5DCE5");

    private static readonly Color CoverageYes = Color.FromHex("#2E7D32");
    private static readonly Color CoverageYesBg = Color.FromHex("#E8F5E9");
    private static readonly Color CoveragePartial = Color.FromHex("#B07B00");
    private static readonly Color CoveragePartialBg = Color.FromHex("#FFF8E1");
    private static readonly Color CoverageNo = Color.FromHex("#C62828");
    private static readonly Color CoverageNoBg = Color.FromHex("#FFEBEE");

    private static readonly Color ScoreHighBg = Color.FromHex("#E8F5E9");
    private static readonly Color ScoreMidBg = Color.FromHex("#FFF8E1");
    private static readonly Color ScoreLowBg = Color.FromHex("#FFEBEE");
    private static readonly Color ScoreNaBg = Color.FromHex("#ECEFF1");

    // Convenience overload used in the real session flow: pulls everything off
    // a live Session and delegates to the snapshot-based overload below. The
    // Unity test driver can't construct a Session, so it calls the snapshot
    // overload directly with synthetic data.
    public static void Export(
        string path,
        Session session,
        DeepDiveResult? deepDiveResult,
        ClarificationResult? clarificationResult = null,
        MainIdeaComparatorResult? mainIdeaComparator = null,
        string? backendName = null,
        DateTimeOffset? sessionEndedAt = null)
    {
        Guard.NotNull(session);

        Export(
            path:                 path,
            transcripts:          session.TranscriptStore.Snapshot(),
            metrics:              session.MetricsStore.Snapshot(),
            qaRounds:             session.QaRoundsHistory.Snapshot(),
            archivedConcerns:     session.ConcernArchive.Snapshot(),
            retellings:           session.RetellingsHistory.Snapshot(),
            sessionTimeLimit:     session.Config.TimeLimit,
            deepDiveResult:       deepDiveResult,
            clarificationResult:  clarificationResult,
            mainIdeaComparator:   mainIdeaComparator,
            backendName:          backendName,
            sessionEndedAt:       sessionEndedAt);
    }

    // Snapshot-based overload — the canonical implementation. Takes only the
    // data the document needs (no Session reference) so callers without a
    // live Session (e.g. ResultsPanelTestDriver in Unity) can drive the
    // exact same layout as production.
    public static void Export(
        string path,
        IReadOnlyList<TranscriptEntry> transcripts,
        IReadOnlyList<MetricEntry> metrics,
        IReadOnlyList<QaRoundEntry> qaRounds,
        IReadOnlyList<ArchivedConcernEntry> archivedConcerns,
        IReadOnlyList<RetellingHistoryEntry> retellings,
        TimeSpan? sessionTimeLimit,
        DeepDiveResult? deepDiveResult,
        ClarificationResult? clarificationResult = null,
        MainIdeaComparatorResult? mainIdeaComparator = null,
        string? backendName = null,
        DateTimeOffset? sessionEndedAt = null)
    {
        Guard.NotNullOrWhiteSpace(path);
        Guard.NotNull(transcripts);
        Guard.NotNull(metrics);
        Guard.NotNull(qaRounds);
        Guard.NotNull(archivedConcerns);
        Guard.NotNull(retellings);

        QuestPDF.Settings.License = LicenseType.Community;

        var endedAt = sessionEndedAt ?? DateTimeOffset.Now;

        var metricsBySeq = new Dictionary<long, MetricEntry>(metrics.Count);
        foreach (var m in metrics) metricsBySeq[m.Sequence] = m;

        var concernTypeLookup = BuildConcernTypeLookup(archivedConcerns, retellings);
        var resolvedIds       = BuildResolvedIdsLookup(retellings);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.Margin(36);
                page.DefaultTextStyle(x => x.FontSize(10).LineHeight(1.25f));

                page.Header().Element(c => ComposeCoverHeader(c, sessionTimeLimit, endedAt, backendName));

                page.Content().PaddingTop(16).Column(col =>
                {
                    col.Spacing(18);

                    ComposeTranscriptSection(col.Item(), transcripts, metricsBySeq);
                    ComposeQaRoundsSection(col.Item(), qaRounds, transcripts, concernTypeLookup, resolvedIds);
                    ComposeDeepDiveSection(col.Item(), deepDiveResult);
                    ComposeMainIdeaAnalysisSection(col.Item(), mainIdeaComparator);
                    ComposeClarificationSection(col.Item(), clarificationResult);
                });

                page.Footer().AlignCenter().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(8).FontColor(SubtleText));
                    text.Span("GemmaStage Session Report · page ");
                    text.CurrentPageNumber();
                    text.Span(" of ");
                    text.TotalPages();
                });
            });
        })
        .GeneratePdf(path);
    }

    // ── Cover header ────────────────────────────────────────────────────────

    private static void ComposeCoverHeader(
        QuestPDF.Infrastructure.IContainer container,
        TimeSpan? sessionTimeLimit,
        DateTimeOffset endedAt,
        string? backendName)
    {
        container.Background(HeaderBg).PaddingVertical(14).PaddingHorizontal(16).Column(col =>
        {
            col.Item().Text("GemmaStage Session Report")
                .FontSize(20).Bold().FontColor(HeaderText);

            col.Item().PaddingTop(4).Row(row =>
            {
                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(9).FontColor(HeaderText.WithAlpha(0.85f)));
                    text.Span("Completed: ").SemiBold();
                    text.Span(endedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
                });

                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(9).FontColor(HeaderText.WithAlpha(0.85f)));
                    text.Span("Target duration: ").SemiBold();
                    text.Span(FormatTimeLimit(sessionTimeLimit));
                });

                row.RelativeItem().Text(text =>
                {
                    text.DefaultTextStyle(x => x.FontSize(9).FontColor(HeaderText.WithAlpha(0.85f)));
                    text.Span("Backend: ").SemiBold();
                    text.Span(string.IsNullOrEmpty(backendName) ? "(unknown)" : backendName!);
                });
            });
        });
    }

    private static string FormatTimeLimit(TimeSpan? limit)
    {
        if (!limit.HasValue) return "(unlimited)";
        var t = limit.Value;
        if (t.TotalMinutes < 60) return $"{t.Minutes} min";
        return $"{(int)t.TotalHours}h {t.Minutes:00}m";
    }

    // ── Transcript section ─────────────────────────────────────────────────

    private static void ComposeTranscriptSection(
        QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<TranscriptEntry> transcripts,
        IReadOnlyDictionary<long, MetricEntry> metricsBySeq)
    {
        container.Column(col =>
        {
            ComposeSectionHeader(col.Item(), "1", "Transcript",
                transcripts.Count > 0
                    ? $"{transcripts.Count} audio chunk(s)"
                    : "(no audio recorded)");

            if (transcripts.Count == 0) return;

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(72);   // timestamp
                    c.RelativeColumn(3);     // text
                    c.ConstantColumn(120);   // metadata badges
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Time");
                    header.Cell().Element(HeaderCell).Text("Transcript");
                    header.Cell().Element(HeaderCell).Text("Audio cues");
                });

                for (int i = 0; i < transcripts.Count; i++)
                {
                    var t = transcripts[i];
                    var rowBg = i % 2 == 1 ? ZebraBg : Colors.White;

                    table.Cell().Background(rowBg).Padding(6).Text(FormatClockTime(t.Timestamp))
                        .FontSize(9).FontColor(SubtleText).FontFamily("Courier New");
                    table.Cell().Background(rowBg).Padding(6).Text(string.IsNullOrWhiteSpace(t.Text) ? "—" : t.Text)
                        .FontSize(10);

                    if (metricsBySeq.TryGetValue(t.Sequence, out var m))
                    {
                        table.Cell().Background(rowBg).Padding(6).Text(FormatAudioCues(m))
                            .FontSize(8).FontColor(SubtleText);
                    }
                    else
                    {
                        table.Cell().Background(rowBg);
                    }
                }
            });
        });
    }

    private static string FormatAudioCues(MetricEntry m)
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrEmpty(m.Clarity)) parts.Add($"clarity: {m.Clarity}");
        if (!string.IsNullOrEmpty(m.Emotion)) parts.Add($"emotion: {m.Emotion}");
        if (!string.IsNullOrEmpty(m.Grammar)) parts.Add($"grammar: {m.Grammar}");
        return parts.Count == 0 ? string.Empty : string.Join("\n", parts);
    }

    // ── Q&A Rounds section ─────────────────────────────────────────────────

    private static void ComposeQaRoundsSection(
        QuestPDF.Infrastructure.IContainer container,
        IReadOnlyList<QaRoundEntry> rounds,
        IReadOnlyList<TranscriptEntry> transcripts,
        IReadOnlyDictionary<long, InquirerConcernType> concernType,
        ISet<long> resolvedIds)
    {
        container.Column(col =>
        {
            ComposeSectionHeader(col.Item(), "2", "Q&A Rounds",
                rounds.Count > 0
                    ? $"{rounds.Count} round(s)"
                    : "(no Q&A rounds occurred)");

            if (rounds.Count == 0) return;

            for (int i = 0; i < rounds.Count; i++)
            {
                var r = rounds[i];
                var durationSec = (r.ClosedAt - r.OpenedAt).TotalSeconds;
                var answer = SliceTranscriptByWindow(transcripts, r.OpenedAt, r.ClosedAt);
                concernType.TryGetValue(r.ConcernId, out var type);
                var resolved = resolvedIds.Contains(r.ConcernId);

                col.Item().PaddingTop(i == 0 ? 6 : 10).Border(0.5f).BorderColor(DividerColor)
                    .Background(ZebraBg).Padding(10).Column(box =>
                {
                    box.Item().Row(row =>
                    {
                        row.RelativeItem().Text(text =>
                        {
                            text.Span($"Round {i + 1} ").SemiBold().FontSize(11);
                            text.Span($"· {r.Phase}").FontSize(10).FontColor(SubtleText);
                            text.Span($"  ·  {FormatConcernType(type)}").FontSize(10).FontColor(SubtleText);
                        });

                        row.ConstantItem(160).AlignRight().Text(text =>
                        {
                            text.DefaultTextStyle(x => x.FontSize(8).FontColor(SubtleText));
                            text.Span(FormatClockTime(r.OpenedAt));
                            text.Span(" → ");
                            text.Span(FormatClockTime(r.ClosedAt));
                            text.Span($"  ({durationSec:0.0}s)");
                        });
                    });

                    box.Item().PaddingTop(4).Text(text =>
                    {
                        text.Span("Q: ").SemiBold().FontColor(SectionAccent);
                        text.Span(string.IsNullOrWhiteSpace(r.QuestionText) ? "(no question text)" : r.QuestionText);
                    });

                    box.Item().PaddingTop(2).Text(text =>
                    {
                        text.Span("A: ").SemiBold().FontColor(SectionAccent);
                        text.Span(string.IsNullOrWhiteSpace(answer) ? "(speaker did not answer)" : answer);
                    });

                    box.Item().PaddingTop(4).AlignRight().Text(text =>
                    {
                        if (resolved)
                            text.Span("✓ Resolved").FontSize(8).SemiBold().FontColor(CoverageYes);
                        else
                            text.Span("⊘ Not answered").FontSize(8).SemiBold().FontColor(CoverageNo);
                    });
                });
            }
        });
    }

    private static Dictionary<long, InquirerConcernType> BuildConcernTypeLookup(
        IReadOnlyList<ArchivedConcernEntry> archivedConcerns,
        IReadOnlyList<RetellingHistoryEntry> retellings)
    {
        var lookup = new Dictionary<long, InquirerConcernType>();
        foreach (var c in archivedConcerns)
        {
            lookup[c.Id] = c.Type;
        }
        // The reflections also carry currently-open concerns — capture their
        // type in case a round closed for a concern that wasn't archived (rare,
        // but happens for resolved concerns since "resolved" archives them).
        foreach (var r in retellings)
        {
            foreach (var nc in r.Reflection.NewConcerns)
            {
                lookup[nc.Id] = nc.Type;
            }
        }
        return lookup;
    }

    private static HashSet<long> BuildResolvedIdsLookup(IReadOnlyList<RetellingHistoryEntry> retellings)
    {
        var resolved = new HashSet<long>();
        foreach (var r in retellings)
        {
            foreach (var removed in r.Reflection.RemovedConcerns)
            {
                if (removed.Cause == RemovedConcernCause.Resolved)
                {
                    resolved.Add(removed.Id);
                }
            }
        }
        return resolved;
    }

    private static string SliceTranscriptByWindow(
        IReadOnlyList<TranscriptEntry> transcripts,
        DateTimeOffset openedAt,
        DateTimeOffset closedAt)
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

    private static string FormatConcernType(InquirerConcernType type) => type switch
    {
        InquirerConcernType.TopicUnknown => "Topic unknown",
        InquirerConcernType.ComprehensionGap => "Comprehension gap",
        InquirerConcernType.DetailRequest => "Detail request",
        _ => "—",
    };

    // ── DeepDive section ───────────────────────────────────────────────────

    private static readonly (string Name, Func<DeepDiveResult, DeepDiveCriterion> Get)[] _criteria = new (string, Func<DeepDiveResult, DeepDiveCriterion>)[]
    {
        ("Main Idea Clarity",       r => r.MainIdeaClarity),
        ("Structure",                r => r.Structure),
        ("Consistency & Focus",      r => r.ConsistencyFocus),
        ("Support & Justification",  r => r.SupportJustification),
        ("Language Quality",         r => r.LanguageQuality),
        ("Emotional Delivery",       r => r.EmotionalDelivery),
        ("Q&A Handling",             r => r.QaHandling),
    };

    private static void ComposeDeepDiveSection(
        QuestPDF.Infrastructure.IContainer container,
        DeepDiveResult? result)
    {
        container.Column(col =>
        {
            ComposeSectionHeader(col.Item(), "3", "DeepDive Evaluation",
                result is null ? "(not run)" : null);

            if (result is null) return;

            // ── Summary table ──
            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.RelativeColumn(3);
                    c.ConstantColumn(60);
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("Criterion");
                    header.Cell().Element(HeaderCell).AlignCenter().Text("Score");
                });

                foreach (var (name, get) in _criteria)
                {
                    var crit = get(result);
                    table.Cell().Padding(6).Text(name).FontSize(10);
                    table.Cell().Element(c => c.Background(ScoreBackground(crit.Value)).Padding(6).AlignCenter())
                        .Text(FormatScore(crit.Value)).SemiBold().FontSize(11);
                }
            });

            // ── Per-criterion detail blocks ──
            col.Item().PaddingTop(10).Text("Per-criterion verdicts")
                .FontSize(11).SemiBold().FontColor(SectionAccent);

            foreach (var (name, get) in _criteria)
            {
                var crit = get(result);
                col.Item().PaddingTop(8).Border(0.5f).BorderColor(DividerColor)
                    .Background(Colors.White).Padding(10).Column(box =>
                {
                    box.Item().Row(row =>
                    {
                        row.RelativeItem().Text(name).SemiBold().FontSize(12).FontColor(SectionAccent);
                        row.ConstantItem(48).Background(ScoreBackground(crit.Value))
                            .AlignCenter().AlignMiddle().Height(24)
                            .Text(FormatScore(crit.Value)).SemiBold().FontSize(12);
                    });

                    box.Item().PaddingTop(4).Text(string.IsNullOrWhiteSpace(crit.Verdict)
                        ? "(no verdict produced)"
                        : crit.Verdict).FontSize(10);
                });
            }
        });
    }

    private static string FormatScore(DeepDiveScore score) => score == DeepDiveScore.NotApplicable
        ? "N/A"
        : ((int)score).ToString();

    private static Color ScoreBackground(DeepDiveScore score) => score switch
    {
        DeepDiveScore.Five or DeepDiveScore.Four => ScoreHighBg,
        DeepDiveScore.Three => ScoreMidBg,
        DeepDiveScore.Two or DeepDiveScore.One => ScoreLowBg,
        _ => ScoreNaBg,
    };

    // ── Main Idea Analysis section ─────────────────────────────────────────

    private static void ComposeMainIdeaAnalysisSection(
        QuestPDF.Infrastructure.IContainer container,
        MainIdeaComparatorResult? result)
    {
        if (result?.Output is not { } cmp)
        {
            // No ground-truth doc was attached (or comparator skipped) — render
            // nothing rather than an empty section.
            container.Column(_ => { });
            return;
        }

        container.Column(col =>
        {
            ComposeSectionHeader(col.Item(), "4", "Main Idea Analysis",
                $"recall {cmp.Recall:P0} across {cmp.AnchorClaims.Count} anchor claim(s)");

            col.Item().PaddingTop(6).Column(box =>
            {
                box.Spacing(6);

                box.Item().Element(c => RenderThesisRow(c, "Anchor thesis", cmp.AnchorThesis));
                box.Item().Element(c => RenderThesisRow(c, "Audience thesis", cmp.AudienceThesis));
                box.Item().Element(c => RenderThesisRow(c, "Comparison", cmp.ThesisComparison));
            });

            if (cmp.ClaimCoverages.Count > 0)
            {
                col.Item().PaddingTop(10).Text("Claim coverage")
                    .FontSize(11).SemiBold().FontColor(SectionAccent);

                col.Item().PaddingTop(4).Table(table =>
                {
                    table.ColumnsDefinition(c =>
                    {
                        c.ConstantColumn(60);   // coverage chip
                        c.RelativeColumn(3);     // claim
                        c.RelativeColumn(3);     // evidence
                    });

                    table.Header(header =>
                    {
                        header.Cell().Element(HeaderCell).Text("Coverage");
                        header.Cell().Element(HeaderCell).Text("Anchor claim");
                        header.Cell().Element(HeaderCell).Text("Evidence");
                    });

                    foreach (var c in cmp.ClaimCoverages)
                    {
                        var bg = CoverageRowBackground(c.Coverage);
                        var fg = CoverageRowText(c.Coverage);

                        table.Cell().Background(bg).Padding(6).AlignCenter()
                            .Text(c.Coverage.ToString().ToLowerInvariant())
                            .FontSize(9).SemiBold().FontColor(fg);
                        table.Cell().Background(bg).Padding(6).Text(c.AnchorClaim).FontSize(10);
                        table.Cell().Background(bg).Padding(6)
                            .Text(string.IsNullOrWhiteSpace(c.Evidence) ? "—" : c.Evidence!)
                            .FontSize(10).FontColor(SubtleText);
                    }
                });
            }
        });
    }

    private static void RenderThesisRow(
        QuestPDF.Infrastructure.IContainer container,
        string label,
        string value)
    {
        container.Row(row =>
        {
            row.ConstantItem(110).Text(label).SemiBold().FontSize(10).FontColor(SubtleText);
            row.RelativeItem().Text(string.IsNullOrWhiteSpace(value) ? "—" : value).FontSize(10);
        });
    }

    private static Color CoverageRowBackground(CoverageLabel coverage) => coverage switch
    {
        CoverageLabel.Yes => CoverageYesBg,
        CoverageLabel.Partial => CoveragePartialBg,
        CoverageLabel.No => CoverageNoBg,
        _ => Colors.White,
    };

    private static Color CoverageRowText(CoverageLabel coverage) => coverage switch
    {
        CoverageLabel.Yes => CoverageYes,
        CoverageLabel.Partial => CoveragePartial,
        CoverageLabel.No => CoverageNo,
        _ => SubtleText,
    };

    // ── Clarification section ──────────────────────────────────────────────

    private static void ComposeClarificationSection(
        QuestPDF.Infrastructure.IContainer container,
        ClarificationResult? result)
    {
        if (result is null || !result.Ran || result.RevisedQuestions.Count == 0)
        {
            container.Column(_ => { });
            return;
        }

        var resolvedIds = new HashSet<long>(result.ResolvedIds);
        int resolvedCount = result.RevisedQuestions.Count(q => resolvedIds.Contains(q.Id));

        container.Column(col =>
        {
            ComposeSectionHeader(col.Item(), "5", "Clarification",
                $"{result.RevisedQuestions.Count} revised question(s), {resolvedCount} resolved");

            col.Item().PaddingTop(6).Table(table =>
            {
                table.ColumnsDefinition(c =>
                {
                    c.ConstantColumn(36);    // id
                    c.ConstantColumn(110);   // type
                    c.RelativeColumn(5);      // question
                    c.ConstantColumn(80);    // status
                });

                table.Header(header =>
                {
                    header.Cell().Element(HeaderCell).Text("ID");
                    header.Cell().Element(HeaderCell).Text("Type");
                    header.Cell().Element(HeaderCell).Text("Revised question");
                    header.Cell().Element(HeaderCell).AlignCenter().Text("Status");
                });

                int i = 0;
                foreach (var q in result.RevisedQuestions)
                {
                    var bg = i++ % 2 == 1 ? ZebraBg : Colors.White;
                    var resolved = resolvedIds.Contains(q.Id);

                    table.Cell().Background(bg).Padding(6).Text($"#{q.Id}").FontSize(9).FontColor(SubtleText);
                    table.Cell().Background(bg).Padding(6).Text(FormatConcernType(q.Type)).FontSize(9);
                    table.Cell().Background(bg).Padding(6).Text(q.Question).FontSize(10);
                    table.Cell().Background(bg).Padding(6).AlignCenter().Text(resolved ? "Resolved" : "Unresolved")
                        .SemiBold().FontSize(9).FontColor(resolved ? CoverageYes : CoverageNo);
                }
            });
        });
    }

    // ── Shared helpers ─────────────────────────────────────────────────────

    private static void ComposeSectionHeader(
        QuestPDF.Infrastructure.IContainer container,
        string number,
        string title,
        string? subtitle)
    {
        container.Column(col =>
        {
            col.Item().BorderBottom(1.5f).BorderColor(SectionAccent).PaddingBottom(4)
                .Row(row =>
                {
                    row.ConstantItem(28).Background(SectionAccent).AlignCenter().AlignMiddle().Height(24)
                        .Text(number).FontColor(Colors.White).SemiBold().FontSize(12);
                    row.RelativeItem().PaddingLeft(8).AlignMiddle().Text(title)
                        .FontSize(14).SemiBold().FontColor(SectionAccent);

                    if (!string.IsNullOrEmpty(subtitle))
                    {
                        row.ConstantItem(220).AlignRight().AlignMiddle().Text(subtitle!)
                            .FontSize(9).Italic().FontColor(SubtleText);
                    }
                });
        });
    }

    private static QuestPDF.Infrastructure.IContainer HeaderCell(QuestPDF.Infrastructure.IContainer container)
        => container.Background(TableHeaderBg).BorderBottom(0.75f).BorderColor(DividerColor)
            .Padding(6).DefaultTextStyle(x => x.SemiBold().FontSize(9).FontColor(SectionAccent));

    private static string FormatClockTime(DateTimeOffset timestamp)
        => timestamp.ToLocalTime().ToString("HH:mm:ss");
}
