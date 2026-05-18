using System;
using GemmaStage.Session.Native;

namespace GemmaStage.Session.Resilience;

public static class ToolCallRetry
{
    public const int AttemptsPerPhase = 3;

    public static T Execute<T>(
        string role,
        ConversationConfigHandle config,
        bool initialThinking,
        Func<T> attempt,
        Action<string>? warn) where T : IToolCallParseResult
    {
        Guard.NotNullOrWhiteSpace(role);
        Guard.NotNull(config);
        Guard.NotNull(attempt);

        var result = RunPhase(role, "primary", initialThinking, attempt, warn, out var lastError);
        if (!result.IsFailure)
        {
            return result;
        }

        if (initialThinking)
        {
            GemmaStageNative.ConversationConfigSetEnableThinking(config, false);
        }
        try
        {
            result = RunPhase(role, "no-thinking", thinking: false, attempt, warn, out lastError);
            if (!result.IsFailure)
            {
                return result;
            }
        }
        finally
        {
            if (initialThinking)
            {
                GemmaStageNative.ConversationConfigSetEnableThinking(config, true);
            }
        }

        throw new ToolCallFailureException(role, AttemptsPerPhase * 2, lastError);
    }

    private static T RunPhase<T>(
        string role,
        string phase,
        bool thinking,
        Func<T> attempt,
        Action<string>? warn,
        out string lastError) where T : IToolCallParseResult
    {
        T result = default!;
        lastError = "unknown";
        for (var i = 1; i <= AttemptsPerPhase; i++)
        {
            result = attempt();
            if (!result.IsFailure)
            {
                return result;
            }
            lastError = result.Error ?? "unknown";
            warn?.Invoke($"[{role}] phase={phase} attempt={i}/{AttemptsPerPhase} thinking={thinking} failed: {lastError}");
        }
        return result;
    }
}
