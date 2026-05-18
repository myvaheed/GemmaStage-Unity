namespace GemmaStage.Session.PoC;

// perceptor.jsonl
internal sealed record PerceptorLogEntry(
    double TsMs,
    string Kind,
    double? InputSec,
    object? OutputJson,
    int? DecodeTokens,
    double? DecodeMs,
    double? TtftMs);

// inquirer.jsonl
internal sealed record InquirerLogEntry(
    double TsMs,
    int CycleIndex,
    string? RetellingExcerpt,
    bool ReflectionOk,
    string? Error,
    object? ReflectionJson,
    string? RawResponseJson);

// cycle.jsonl
internal sealed record CycleLogEntry(
    double TsMs,
    int QueueDepthAtPause,
    double DrainTimeMs,
    int DrainedCount);

// transcript_summarizer.jsonl
internal sealed record TranscriptSummarizerLogEntry(
    double TsMs,
    string Stage,
    int? ChunkIndex,
    int? InputSize,
    object? OutputJson);

// ground_truth.jsonl
internal sealed record GroundTruthSummarizerLogEntry(
    double TsMs,
    bool Ran,
    string? SourcePath,
    int? OutputLength,
    string? Error,
    object? OutputJson);

// deepdive.jsonl
internal sealed record DeepDiveLogEntry(
    double TsMs,
    int InputSize,
    object? EvaluationResult);

// clarification.jsonl
internal sealed record ClarificationLogEntry(
    double TsMs,
    int OriginalArchivedCount,
    int RevisedCount,
    int ResolvedCount,
    int UnresolvedCount,
    object UnresolvedConcerns);

// metrics.jsonl — record type moved to GemmaStage.Session.Export.MetricsLogEntry
// so the xlsx exporter (now in the session module) can share the shape with
// both the PoC's JSONL writer and the Unity SessionLayer.
