using System.Collections.Generic;

namespace GemmaStage.Session.GroundTruthSummarizer;

// Per-chunk MAP delta — only the new claims this chunk added beyond what
// earlier chunks had captured.
public sealed record GroundTruthSummarizerMapOutput(IReadOnlyList<string> Claims);

// Final REDUCE output — the document's thesis plus the deduplicated final
// claim list. Consumed by MainIdeaComparator (1 thesis call + N claim-coverage
// calls).
public sealed record GroundTruthSummarizerOutput(
    string MainThesis,
    IReadOnlyList<string> Claims);
