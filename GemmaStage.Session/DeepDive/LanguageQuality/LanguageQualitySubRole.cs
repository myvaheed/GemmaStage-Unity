using System.Collections.Generic;
using System.Linq;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.LanguageQuality;

public sealed record LanguageQualityInput(
    IReadOnlyList<TranscriptSlice> WeakSlices,
    ComputedLanguageQuality Computed,
    GrammarDynamics Distribution);

public static class LanguageQualitySchemas
{
    public const string ToolName = "report_language_quality";
}

public static class LanguageQualityInputFormatter
{
    public static string Format(LanguageQualityInput input)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== GRAMMAR DISTRIBUTION ===");
        if (input.Distribution.distribution.Count == 0)
        {
            sb.AppendLine("  (no grammar data)");
        }
        else
        {
            sb.Append("  Overall: ").AppendLine(input.Distribution.overall);
            sb.Append("  Distribution: ").AppendLine(
                string.Join(", ", input.Distribution.distribution.Select(kvp => $"{kvp.Key}={kvp.Value}")));
        }
        sb.AppendLine();

        sb.AppendLine("=== COMPUTED LANGUAGE QUALITY ===");
        sb.Append("  Score: ").Append(input.Computed.value).AppendLine();
        sb.Append("  Label: ").AppendLine(input.Computed.label);
        sb.AppendLine();

        sb.AppendLine("=== POOR/MODERATE-GRAMMAR TRANSCRIPT SLICES ===");
        if (input.WeakSlices.Count == 0)
        {
            sb.AppendLine("  (no chunks tagged poor or moderate)");
        }
        else
        {
            foreach (var s in input.WeakSlices)
            {
                sb.Append("  - ").AppendLine(s.Text);
            }
        }

        return sb.ToString();
    }
}

public static class LanguageQualityResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, LanguageQualitySchemas.ToolName, acceptZero: false);
    }
}

public sealed class LanguageQualityConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private LanguageQualityConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static LanguageQualityConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new LanguageQualityConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                LanguageQualityPrompts.System,
                LanguageQualityPrompts.ToolsJson,
                LanguageQualityPrompts.EnableThinking,
                role: "DeepDive.LanguageQuality",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(LanguageQualityInput input)
    {
        Guard.NotNull(input);
        var prefill = LanguageQualityInputFormatter.Format(input).TrimEnd();
        var prompt = LanguageQualityPrompts.UserTemplate
            .Replace("{tool_name}", LanguageQualitySchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, LanguageQualityResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
