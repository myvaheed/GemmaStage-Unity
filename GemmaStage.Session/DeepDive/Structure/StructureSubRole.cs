using System.Collections.Generic;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.Structure;

public sealed record StructureInput(
    IReadOnlyList<StructureChunkRow> Rows,
    IReadOnlyList<ConfusionDynamicsPoint> ConfusionDynamics,
    ComputedScore Computed);

public static class StructureSchemas
{
    public const string ToolName = "report_structure";
}

public static class StructureInputFormatter
{
    public static string Format(StructureInput input)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== COMPUTED SCORE ===");
        sb.Append("  Score: ").Append(input.Computed.Value).AppendLine();
        sb.Append("  Label: ").AppendLine(input.Computed.Label);
        sb.AppendLine();

        sb.AppendLine("=== CHUNK STRUCTURE LABELS ===");
        if (input.Rows.Count == 0)
        {
            sb.AppendLine("(no chunks)");
        }
        else
        {
            foreach (var r in input.Rows)
            {
                sb.Append("Chunk ").Append(r.Index).Append(" [").Append(r.StructureLabel).Append("]: ").AppendLine(r.Retelling);
            }
        }
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

        return sb.ToString();
    }

}

public static class StructureResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, StructureSchemas.ToolName, acceptZero: false);
    }
}

public sealed class StructureConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private StructureConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static StructureConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new StructureConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                StructurePrompts.System,
                StructurePrompts.ToolsJson,
                StructurePrompts.EnableThinking,
                role: "DeepDive.Structure",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(StructureInput input)
    {
        Guard.NotNull(input);
        var prefill = StructureInputFormatter.Format(input).TrimEnd();
        var prompt = StructurePrompts.UserTemplate
            .Replace("{tool_name}", StructureSchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, StructureResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
