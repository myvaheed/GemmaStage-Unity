using System.Collections.Generic;

namespace GemmaStage.Session.TranscriptSummarizer;

public static class TranscriptSummarizerSchemas
{
    // MAP runs in two passes per chunk: a retelling-only call followed by an
    // enum-tag-only call. Splitting them keeps each schema single-purpose so
    // the small model never has to mix free-text with enums in one shot.
    public const string MapRetellingToolName = "report_chunk_retelling";
    public const string MapSignalsToolName = "report_chunk_signals";
    public const string ReduceToolName = "report_transcript_summary";

    public static readonly IReadOnlyList<string> StructureLabels = new[]
    {
        "intro",
        "development",
        "conclusion",
        "unclear",
    };

    public static readonly IReadOnlyList<string> ConsistencyLabels = new[]
    {
        "consistent",
        "minor_drift",
        "major_drift",
    };

    public static readonly IReadOnlyList<string> SupportLabels = new[]
    {
        "none",
        "weak",
        "moderate",
        "strong",
    };

    public const int NotesMaxItems = 3;
}
