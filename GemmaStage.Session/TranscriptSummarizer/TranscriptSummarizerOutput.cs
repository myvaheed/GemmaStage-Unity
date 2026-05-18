using System.Collections.Generic;

namespace GemmaStage.Session.TranscriptSummarizer;

// Intermediate per-call records. The MAP stage runs in two passes per chunk
// (a retelling-only call and an enum-tag-only call); the runner combines the
// two into TranscriptSummarizerMapOutput before adding it to the result list.
public sealed record TranscriptSummarizerMapRetellingOutput(string Retelling);

public sealed record TranscriptSummarizerMapSignalsOutput(
    string Structure,
    string Consistency,
    string Support,
    IReadOnlyList<string> Notes);

public sealed record TranscriptSummarizerMapOutput(
    string Retelling,
    string Structure,
    string Consistency,
    string Support,
    IReadOnlyList<string> Notes);

public sealed record TranscriptSummarizerReduceOutput(
    string InferredMainIdeaFromTranscript);
