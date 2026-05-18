using System;

namespace GemmaStage.Session.Resilience;

public sealed class ToolCallFailureException : Exception
{
    public string Role { get; }
    public int Attempts { get; }
    public string LastError { get; }

    public ToolCallFailureException(string role, int attempts, string lastError)
        : base($"{role}: no valid tool call after {attempts} attempts. Last error: {lastError}")
    {
        Role = role;
        Attempts = attempts;
        LastError = lastError;
    }
}
