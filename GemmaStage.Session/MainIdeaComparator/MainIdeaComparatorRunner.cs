using GemmaStage.Session.GroundTruthSummarizer;
using GemmaStage.Session.IdeaReflector;
using GemmaStage.Session.Native;
using System;
using System.Collections.Generic;

namespace GemmaStage.Session.MainIdeaComparator;

public sealed record MainIdeaComparatorResult(
    bool Ran,
    MainIdeaComparatorOutput? Output,
    MainIdeaComparatorTurnResult? ThesisTurn,
    IReadOnlyList<MainIdeaComparatorTurnResult> CoverageTurns,
    string? SkipReason)
{
    public static MainIdeaComparatorResult Skipped(string reason) => new(
        Ran: false,
        Output: null,
        ThesisTurn: null,
        CoverageTurns: Array.Empty<MainIdeaComparatorTurnResult>(),
        SkipReason: reason);
}

// Anchor-driven coverage. Runs only when ground truth is present.
//   1. One thesis-comparison call: anchor.thesis vs audience.thesis.
//   2. N coverage calls — one per GT claim — each judging whether the audience's
//      cumulative main_idea_understanding paragraph supports that anchor claim.
//   3. Recall = Σ score(coverage) / N where yes=1.0, partial=0.5, no=0.0.
public sealed class MainIdeaComparatorRunner
{
    private readonly EngineHandle _engine;
    private readonly Action<string>? _warn;

    public MainIdeaComparatorRunner(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;
    }

    public MainIdeaComparatorResult Run(
        AudienceSideRecall? audience,
        GroundTruthSummarizerOutput? groundTruth)
    {
        if (audience is null)
        {
            return MainIdeaComparatorResult.Skipped(
                "No audience-side recall available (IdeaReflector never produced one).");
        }

        if (groundTruth is null ||
            string.IsNullOrWhiteSpace(groundTruth.MainThesis) ||
            groundTruth.Claims.Count == 0)
        {
            return MainIdeaComparatorResult.Skipped(
                "No ground-truth anchor available; comparator only runs when GT is present.");
        }

        if (string.IsNullOrWhiteSpace(audience.Thesis))
        {
            return MainIdeaComparatorResult.Skipped(
                "Audience-side thesis is empty; nothing to compare.");
        }

        if (string.IsNullOrWhiteSpace(audience.MainIdeaUnderstanding))
        {
            return MainIdeaComparatorResult.Skipped(
                "Audience-side main_idea_understanding is empty; coverage cannot be evaluated.");
        }

        // Step 1 — thesis comparison (one call).
        MainIdeaComparatorTurnResult thesisTurn;
        using (var thesis = MainIdeaComparatorConversation.CreateThesis(_engine, _warn))
        {
            thesisTurn = thesis.SendThesis(groundTruth.MainThesis, audience.Thesis);
        }

        var thesisComparison = thesisTurn.Parse.IsThesis && thesisTurn.Parse.Thesis is { } parsedThesis
            ? parsedThesis.ThesisComparison
            : "(thesis comparison call failed; no comparison available.)";

        if (!thesisTurn.Parse.IsThesis)
        {
            Warn($"MainIdeaComparator thesis call failed: {thesisTurn.Parse.Error ?? "(no error)"}");
        }

        // Step 2 — N coverage calls, one per anchor claim.
        var coverageTurns = new List<MainIdeaComparatorTurnResult>(groundTruth.Claims.Count);
        var coverages = new List<ClaimCoverage>(groundTruth.Claims.Count);

        using (var coverage = MainIdeaComparatorConversation.CreateCoverage(_engine, _warn))
        {
            for (int i = 0; i < groundTruth.Claims.Count; i++)
            {
                var anchorClaim = groundTruth.Claims[i];
                var turn = coverage.SendCoverage(anchorClaim, audience.MainIdeaUnderstanding);
                coverageTurns.Add(turn);

                CoverageLabel label;
                string? evidence;
                if (turn.Parse.IsCoverage && turn.Parse.Coverage is { } parsed)
                {
                    label = parsed.Coverage;
                    evidence = parsed.Evidence;
                }
                else
                {
                    Warn($"MainIdeaComparator coverage call {i + 1}/{groundTruth.Claims.Count} failed; treating as 'no'. Error: {turn.Parse.Error ?? "(no error)"}");
                    label = CoverageLabel.No;
                    evidence = null;
                }

                coverages.Add(new ClaimCoverage(
                    AnchorClaim: anchorClaim,
                    Coverage: label,
                    Evidence: evidence));
            }
        }

        var recall = ComputeRecall(coverages);

        var output = new MainIdeaComparatorOutput(
            AnchorThesis: groundTruth.MainThesis,
            AnchorClaims: groundTruth.Claims,
            AudienceThesis: audience.Thesis,
            AudienceMainIdeaUnderstanding: audience.MainIdeaUnderstanding,
            ThesisComparison: thesisComparison,
            ClaimCoverages: coverages,
            Recall: recall);

        return new MainIdeaComparatorResult(
            Ran: true,
            Output: output,
            ThesisTurn: thesisTurn,
            CoverageTurns: coverageTurns,
            SkipReason: null);
    }

    internal static double ComputeRecall(IReadOnlyList<ClaimCoverage> coverages)
    {
        if (coverages.Count == 0)
        {
            return 0.0;
        }

        double covered = 0.0;
        foreach (var c in coverages)
        {
            covered += c.Coverage switch
            {
                CoverageLabel.Yes => 1.0,
                CoverageLabel.Partial => 0.5,
                _ => 0.0,
            };
        }

        return covered / coverages.Count;
    }

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }
}
