using System;
using System.Collections.Generic;
using System.Text;
using GemmaStage.Session.Native;

namespace GemmaStage.Session.DeepDive;

// Shared input shape for the short-transcript branch of Structure,
// Consistency & Focus, and Support & Justification. When fewer than 3
// chunks are available the deterministic ladders cannot discriminate, so
// each criterion's short prompt scores from raw transcript + chunk
// retellings + audience confusion dynamics directly.
public sealed record DeepDiveShortInput(
    string RawTranscript,
    IReadOnlyList<string> ChunkRetellings,
    IReadOnlyList<ConfusionDynamicsPoint> ConfusionDynamics);

public static class DeepDiveShortInputFormatter
{
    public static string Format(DeepDiveShortInput input)
    {
        Guard.NotNull(input);
        var sb = new StringBuilder();

        sb.AppendLine("=== RAW TRANSCRIPT ===");
        sb.AppendLine(string.IsNullOrWhiteSpace(input.RawTranscript) ? "(no transcript)" : input.RawTranscript);
        sb.AppendLine();

        sb.AppendLine("=== CHUNK RETELLINGS ===");
        if (input.ChunkRetellings.Count == 0)
        {
            sb.AppendLine("(no retellings)");
        }
        else
        {
            for (int i = 0; i < input.ChunkRetellings.Count; i++)
            {
                sb.Append("Chunk ").Append(i + 1).Append(": ").AppendLine(input.ChunkRetellings[i]);
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

// Parameterized conversation used by every short-branch criterion. The
// only per-criterion variation is the system prompt + user template +
// tool name + parser; the rest is identical.
public sealed class DeepDiveShortConversation : IDisposable
{
    private readonly DeepDiveSubRoleEngine _engine;
    private readonly string _userTemplate;
    private readonly string _toolName;
    private readonly Func<string?, DeepDiveSubRoleParseResult> _parse;

    private DeepDiveShortConversation(
        DeepDiveSubRoleEngine engine,
        string userTemplate,
        string toolName,
        Func<string?, DeepDiveSubRoleParseResult> parse)
    {
        _engine = engine;
        _userTemplate = userTemplate;
        _toolName = toolName;
        _parse = parse;
    }

    public static DeepDiveShortConversation Create(
        EngineHandle engine,
        string systemPrompt,
        string toolsJson,
        string userTemplate,
        string toolName,
        Func<string?, DeepDiveSubRoleParseResult> parse,
        string role,
        Action<string>? warn = null)
    {
        return new DeepDiveShortConversation(
            DeepDiveSubRoleEngine.Create(
                engine,
                systemPrompt,
                toolsJson,
                enableThinking: true,
                role: role,
                warn: warn),
            userTemplate,
            toolName,
            parse);
    }

    public DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult> Evaluate(DeepDiveShortInput input)
    {
        Guard.NotNull(input);
        var prefill = DeepDiveShortInputFormatter.Format(input).TrimEnd();
        var prompt = _userTemplate
            .Replace("{tool_name}", _toolName)
            .Replace("{prefill}", prefill);
        return _engine.Evaluate(prompt, _parse);
    }

    public void Dispose() => _engine.Dispose();
}
