using System.Collections.Generic;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.SupportJustification;

public sealed record SupportJustificationInput(
    IReadOnlyList<SupportChunkRow> Rows,
    ComputedScore Computed);

public static class SupportJustificationSchemas
{
    public const string ToolName = "report_support_justification";
}

public static class SupportJustificationInputFormatter
{
    public static string Format(SupportJustificationInput input)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== COMPUTED SCORE ===");
        sb.Append("  Score: ").Append(input.Computed.Value).AppendLine();
        sb.Append("  Label: ").AppendLine(input.Computed.Label);
        sb.AppendLine();

        sb.AppendLine("=== CHUNK SUPPORT LABELS ===");
        if (input.Rows.Count == 0)
        {
            sb.AppendLine("(no chunks)");
        }
        else
        {
            foreach (var r in input.Rows)
            {
                sb.Append("Chunk ").Append(r.Index).Append(" [").Append(r.SupportLabel).Append("]: ").AppendLine(r.Retelling);
            }
        }
        return sb.ToString();
    }
}

public static class SupportJustificationResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, SupportJustificationSchemas.ToolName, acceptZero: false);
    }
}

public sealed class SupportJustificationConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private SupportJustificationConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static SupportJustificationConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new SupportJustificationConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                SupportJustificationPrompts.System,
                SupportJustificationPrompts.ToolsJson,
                SupportJustificationPrompts.EnableThinking,
                role: "DeepDive.SupportJustification",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(SupportJustificationInput input)
    {
        Guard.NotNull(input);
        var prefill = SupportJustificationInputFormatter.Format(input).TrimEnd();
        var prompt = SupportJustificationPrompts.UserTemplate
            .Replace("{tool_name}", SupportJustificationSchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, SupportJustificationResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
