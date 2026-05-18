namespace GemmaStage.Session.Resilience;

public interface IToolCallParseResult
{
    bool IsFailure { get; }
    string? Error { get; }
}
