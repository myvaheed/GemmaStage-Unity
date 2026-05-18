using System.Collections.Generic;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.ConsistencyFocus;

public sealed record ConsistencyFocusInput(
    IReadOnlyList<ConsistencyChunkRow> Rows,
    IReadOnlyList<ConfusionDynamicsPoint> ConfusionDynamics,
    ComputedScore Computed);

public static class ConsistencyFocusSchemas
{
    public const string ToolName = "report_consistency_focus";
}

public static class ConsistencyFocusInputFormatter
{
    public static string Format(ConsistencyFocusInput input)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== COMPUTED SCORE ===");
        sb.Append("  Score: ").Append(input.Computed.Value).AppendLine();
        sb.Append("  Label: ").AppendLine(input.Computed.Label);
        sb.AppendLine();

        sb.AppendLine("=== CHUNK CONSISTENCY LABELS ===");
        if (input.Rows.Count == 0)
        {
            sb.AppendLine("(no chunks)");
        }
        else
        {
            foreach (var r in input.Rows)
            {
                sb.Append("Chunk ").Append(r.Index).Append(" [").Append(r.ConsistencyLabel).Append("]: ").AppendLine(r.Retelling);
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

public static class ConsistencyFocusResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, ConsistencyFocusSchemas.ToolName, acceptZero: false);
    }
}

public sealed class ConsistencyFocusConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private ConsistencyFocusConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static ConsistencyFocusConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new ConsistencyFocusConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                ConsistencyFocusPrompts.System,
                ConsistencyFocusPrompts.ToolsJson,
                ConsistencyFocusPrompts.EnableThinking,
                role: "DeepDive.ConsistencyFocus",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(ConsistencyFocusInput input)
    {
        Guard.NotNull(input);
        var prefill = ConsistencyFocusInputFormatter.Format(input).TrimEnd();
        var prompt = ConsistencyFocusPrompts.UserTemplate
            .Replace("{tool_name}", ConsistencyFocusSchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, ConsistencyFocusResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
