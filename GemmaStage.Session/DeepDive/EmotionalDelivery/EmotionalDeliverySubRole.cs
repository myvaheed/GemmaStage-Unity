using System.Linq;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.EmotionalDelivery;

public sealed record EmotionalDeliveryInput(
    string InferredMainIdea,
    EmotionDynamics Dynamics);

public static class EmotionalDeliverySchemas
{
    public const string ToolName = "report_emotional_delivery";
}

public static class EmotionalDeliveryInputFormatter
{
    public static string Format(EmotionalDeliveryInput input)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== INFERRED MAIN IDEA FROM TRANSCRIPT ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(input.InferredMainIdea) ? "(none)" : input.InferredMainIdea);
        sb.AppendLine();

        sb.AppendLine("=== EMOTION DYNAMICS ===");
        sb.Append("  Overall: ").AppendLine(input.Dynamics.overall);
        sb.Append("  Distribution: ").AppendLine(FormatDistribution(input.Dynamics.distribution));
        sb.AppendLine("  Notes (expressive moments only — calm chunks excluded):");
        if (input.Dynamics.notes.Count == 0)
        {
            sb.AppendLine("    (none)");
        }
        else
        {
            foreach (var n in input.Dynamics.notes)
            {
                sb.Append("    - [").Append(n.emotion).Append("] ").AppendLine(n.transcript);
            }
        }

        return sb.ToString();
    }

    private static string FormatDistribution(System.Collections.Generic.IReadOnlyDictionary<string, int> distribution)
    {
        if (distribution.Count == 0) return "(empty)";
        return string.Join(", ", distribution.Select(kvp => $"{kvp.Key}={kvp.Value}"));
    }
}

public static class EmotionalDeliveryResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, EmotionalDeliverySchemas.ToolName, acceptZero: false);
    }
}

public sealed class EmotionalDeliveryConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private EmotionalDeliveryConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static EmotionalDeliveryConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new EmotionalDeliveryConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                EmotionalDeliveryPrompts.System,
                EmotionalDeliveryPrompts.ToolsJson,
                EmotionalDeliveryPrompts.EnableThinking,
                role: "DeepDive.EmotionalDelivery",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(EmotionalDeliveryInput input)
    {
        Guard.NotNull(input);
        var prefill = EmotionalDeliveryInputFormatter.Format(input).TrimEnd();
        var prompt = EmotionalDeliveryPrompts.UserTemplate
            .Replace("{tool_name}", EmotionalDeliverySchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, EmotionalDeliveryResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
