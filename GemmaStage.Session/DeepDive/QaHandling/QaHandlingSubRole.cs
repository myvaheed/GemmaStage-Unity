using System.Collections.Generic;
using System.Text;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive.QaHandling;

public sealed record QaHandlingInput(IReadOnlyList<QaSpan> Spans);

public static class QaHandlingSchemas
{
    public const string ToolName = "report_qa_handling";
}

public static class QaHandlingInputFormatter
{
    public static string Format(QaHandlingInput input)
    {
        var sb = new StringBuilder();

        sb.AppendLine("=== Q&A ROUNDS ===");
        if (input.Spans.Count == 0)
        {
            sb.AppendLine("(no Q&A rounds occurred)");
            return sb.ToString();
        }

        for (int i = 0; i < input.Spans.Count; i++)
        {
            var s = input.Spans[i];
            var phase = s.Phase == Stores.QaPhase.Live ? "Live" : "Final";
            var resolution = s.Resolution == QaResolution.Resolved ? "Resolved" : "NotAnswered";
            sb.Append("<begin_qa round=\"").Append(i + 1).Append("\" phase=\"").Append(phase)
              .Append("\" resolution=\"").Append(resolution).Append("\">").AppendLine();
            sb.Append("  Question: ").AppendLine(s.QuestionText);
            sb.AppendLine("  Answer:");
            sb.AppendLine(string.IsNullOrWhiteSpace(s.AnswerText) ? "    (no answer audio captured)" : Indent(s.AnswerText, "    "));
            sb.AppendLine("</end_qa>");
        }

        return sb.ToString();
    }

    private static string Indent(string text, string prefix)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            sb.Append(prefix).Append(lines[i]);
            if (i < lines.Length - 1) sb.AppendLine();
        }
        return sb.ToString();
    }
}

public static class QaHandlingResponseParser
{
    public static DeepDiveSubRoleParseResult Parse(string? responseJson)
    {
        return DeepDiveCriterionParsing.ParseSingleCriterion(responseJson, QaHandlingSchemas.ToolName, acceptZero: true);
    }
}

public sealed class QaHandlingConversation : System.IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;

    private QaHandlingConversation(DeepDiveSubRoleEngine engine) { _engine = engine; }

    public static QaHandlingConversation Create(EngineHandle engine, System.Action<string>? warn = null)
    {
        return new QaHandlingConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                QaHandlingPrompts.System,
                QaHandlingPrompts.ToolsJson,
                QaHandlingPrompts.EnableThinking,
                role: "DeepDive.QaHandling",
                warn: warn));
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(QaHandlingInput input)
    {
        Guard.NotNull(input);
        var prefill = QaHandlingInputFormatter.Format(input).TrimEnd();
        var prompt = QaHandlingPrompts.UserTemplate
            .Replace("{tool_name}", QaHandlingSchemas.ToolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, QaHandlingResponseParser.Parse);
    }

    public void Dispose() => _engine.Dispose();
}
