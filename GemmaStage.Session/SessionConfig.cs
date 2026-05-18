using GemmaStage.Session.GroundTruthSummarizer;

namespace GemmaStage.Session;

public sealed record SessionConfig(
    string ModelPath,
    string? MmprojPath = null,
    string? Backend = null,
    int ContextSize = 8192,
    int GpuLayers = -1,
    bool EnableBenchmark = true,
    int NativeLogLevel = 1,
    string Language = "English",
    bool EnableLiveQA = false,
    TimeSpan? TimeLimit = null,
    TimeSpan? CycleMinElapsed = null,
    int ConcernCapacity = GemmaStage.Session.Inquirer.InquirerSchemas.ConcernMaxItems,
    string? InitialTopic = null,
    bool EnableFinalQA = false,
    int ClarificationTokenBudget = 1000,
    // Token budget per TranscriptSummarizer MAP chunk. Lower values produce more chunks
    // and a finer-grained structure_pattern (better chance of detecting
    // intro/conclusion boundaries); higher values reduce MAP cost.
    int TranscriptSummarizerTokenBudget = 500,
    // Optional ground-truth document input. When set to anything other than None,
    // the GroundTruthSummarizer role runs in the post-performance pipeline and
    // produces a ground-truth main idea that becomes the comparator anchor.
    // When None the role is skipped and the comparator falls back to the
    // transcript-side main idea.
    GroundTruthDocInput? GroundTruthInput = null);
