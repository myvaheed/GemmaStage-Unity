using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using GemmaStage.Session.MainIdeaComparator;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.MainIdeaClarity;

public sealed record MainIdeaClarityInput(
    string InferredMainIdea,
    string AudienceSideRecall,
    IReadOnlyList<ConfusionDynamicsPoint> ConfusionDynamics,
    MainIdeaComparatorOutput? Anchor,
    ComputedScore? Computed = null);

public static class MainIdeaClaritySchemas
{
    public const string ToolName = "report_main_idea_clarity";
}

public static class MainIdeaClarityInputFormatter
{
    public static string Format(MainIdeaClarityInput input)
    {
        var sb = new StringBuilder();

        if (input.Computed is { } computed)
        {
            sb.AppendLine("=== COMPUTED SCORE (based on Recall) ===");
            sb.Append("  Score: ").Append(computed.Value).AppendLine();
            sb.Append("  Label: ").AppendLine(computed.Label);
            sb.AppendLine();
        }

        sb.AppendLine("=== INFERRED MAIN IDEA FROM TRANSCRIPT ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(input.InferredMainIdea) ? "(none)" : input.InferredMainIdea);
        sb.AppendLine();

        sb.AppendLine("=== AUDIENCE-SIDE RECALL ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(input.AudienceSideRecall) ? "(none)" : input.AudienceSideRecall);
        sb.AppendLine();

        sb.AppendLine("=== CONFUSION DYNAMICS ===");
        if (input.ConfusionDynamics.Count == 0)
        {
            sb.AppendLine("  (no reflections)");
        }
        else
        {
            foreach (var p in input.ConfusionDynamics)
            {
                sb.Append("  - ").Append(p.phase).Append(": ").AppendLine(p.value);
            }
        }
        sb.AppendLine();

        if (input.Anchor is { } cmp)
        {
            sb.AppendLine("=== MAIN IDEA COMPARATOR ===");
            sb.Append("  Anchor thesis: ").AppendLine(cmp.AnchorThesis);
            sb.Append("  Audience thesis: ").AppendLine(cmp.AudienceThesis);
            sb.Append("  Thesis comparison: ").AppendLine(cmp.ThesisComparison);
            sb.AppendLine("  Per-claim coverage:");
            if (cmp.ClaimCoverages.Count == 0)
            {
                sb.AppendLine("    (no anchor claims)");
            }
            else
            {
                foreach (var c in cmp.ClaimCoverages)
                {
                    sb.Append("    - [").Append(c.Coverage.ToString().ToLowerInvariant()).Append("] ")
                      .Append(c.AnchorClaim);
                    if (!string.IsNullOrWhiteSpace(c.Evidence))
                    {
                        sb.Append(" — evidence: \"").Append(c.Evidence).Append('"');
                    }
                    sb.AppendLine();
                }
            }
            sb.Append("  Recall: ").AppendLine(cmp.Recall.ToString("0.00", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }
}

public static class MainIdeaClarityResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, MainIdeaClaritySchemas.ToolName, acceptZero: false);
    }
}

public sealed class MainIdeaClarityConversation : System.IDisposable
{
    public enum Variant { WithAnchor, NoAnchor }

    private readonly DeepDiveSubRoleEngine _engine;
    private readonly Variant _variant;

    private MainIdeaClarityConversation(DeepDiveSubRoleEngine engine, Variant variant)
    {
        _engine = engine;
        _variant = variant;
    }

    public Variant Mode => _variant;

    public static MainIdeaClarityConversation CreateWithAnchor(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new MainIdeaClarityConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                MainIdeaClarityPrompts.WithAnchorSystem,
                MainIdeaClarityPrompts.ToolsJson,
                MainIdeaClarityPrompts.EnableThinking,
                role: "DeepDive.MainIdeaClarity.WithAnchor",
                warn: warn),
            Variant.WithAnchor);
    }

    public static MainIdeaClarityConversation CreateNoAnchor(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new MainIdeaClarityConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                MainIdeaClarityPrompts.NoAnchorSystem,
                MainIdeaClarityPrompts.ToolsJson,
                MainIdeaClarityPrompts.EnableThinking,
                role: "DeepDive.MainIdeaClarity.NoAnchor",
                warn: warn),
            Variant.NoAnchor);
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(MainIdeaClarityInput input)
    {
        Guard.NotNull(input);
        var prefill = MainIdeaClarityInputFormatter.Format(input).TrimEnd();
        var prompt = MainIdeaClarityPrompts.UserTemplate
            .Replace("{tool_name}", MainIdeaClaritySchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, MainIdeaClarityResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
