namespace GemmaStage.Session.Export;

// Per-stage metrics row consumed by the XLSX "Mem" sheet (and by the PoC's
// metrics.jsonl writer). Moved into the module so both the PoC and the Unity
// SessionLayer can populate the same record shape; Unity passes an empty list
// when the in-game memory sampler is not running.
public sealed record MetricsLogEntry(
    double TsMs,
    string Stage,
    double? TokensPerSec,
    int? DecodeTokens,
    double? DecodeMs,
    double? PrefillTokensPerSec,
    int? PrefillTokens,
    double? PrefillMs,
    double? TemplateBuildMs,
    int? CommitTokens,
    double? CommitMs,
    string? ActiveBackend,
    int? KvTokens,
    int? ContextLimit,
    double? KvCachePercent,
    ulong? VramBytes,
    string? VramSource,
    ulong? RssBytes);
