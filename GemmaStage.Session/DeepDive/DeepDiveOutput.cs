using System;
using System.Collections.Generic;

namespace GemmaStage.Session.DeepDive;

public enum DeepDiveScore
{
    NotApplicable = 0,
    One = 1,
    Two = 2,
    Three = 3,
    Four = 4,
    Five = 5
}

public sealed record DeepDiveCriterion(DeepDiveScore Value, string Verdict);

public sealed record DeepDiveResult(
    DeepDiveCriterion MainIdeaClarity,
    DeepDiveCriterion Structure,
    DeepDiveCriterion ConsistencyFocus,
    DeepDiveCriterion SupportJustification,
    DeepDiveCriterion LanguageQuality,
    DeepDiveCriterion EmotionalDelivery,
    DeepDiveCriterion QaHandling);

// Identifies which criterion a per-role progress event corresponds to.
// Order mirrors DeepDiveResult field order and matches docs/EVALUATION.md.
public enum DeepDiveSubRole
{
    MainIdeaClarity = 0,
    Structure = 1,
    ConsistencyFocus = 2,
    SupportJustification = 3,
    LanguageQuality = 4,
    EmotionalDelivery = 5,
    QaHandling = 6,
}

public sealed record ConfusionDynamicsPoint(string phase, string value);

public sealed record EmotionDynamicsNote(string emotion, string transcript);

public sealed record EmotionDynamics(
    string overall,
    IReadOnlyDictionary<string, int> distribution,
    IReadOnlyList<EmotionDynamicsNote> notes);

public sealed record GrammarDynamicsNote(string grammar, string transcript);

public sealed record GrammarDynamics(
    string overall,
    IReadOnlyDictionary<string, int> distribution,
    IReadOnlyList<GrammarDynamicsNote> notes);

// Programmatically-computed Language Quality verdict derived from the
// grammar-label distribution. value: 1-5 (DeepDiveScore).
public sealed record ComputedLanguageQuality(int value, string label);

// Programmatically-computed score for Structure, Consistency, Support,
// and MainIdeaClarity (WithAnchor). value: 1-5 (DeepDiveScore).
public sealed record ComputedScore(int Value, string Label);

public sealed record StructureChunkRow(int Index, string Retelling, string StructureLabel);

public sealed record ConsistencyChunkRow(int Index, string Retelling, string ConsistencyLabel);

public sealed record SupportChunkRow(int Index, string Retelling, string SupportLabel);

// Slice of raw transcript text used by sub-roles 5 and 6 to ground their
// verdicts in concrete chunk excerpts. Sequence matches TranscriptStore.
public sealed record TranscriptSlice(long Sequence, string Text);

public sealed record EmotionTranscriptSlice(string Emotion, long Sequence, string Text);

public enum QaResolution
{
    Resolved = 0,
    NotAnswered = 1,
}

// One Q&A round for the QA Handling sub-role. Phase distinguishes Live
// from Final rounds; Resolution is decided programmatically from the
// inquirer reflections joined by concern id.
public sealed record QaSpan(
    Stores.QaPhase Phase,
    string QuestionText,
    string AnswerText,
    QaResolution Resolution);

// Aggregate of every per-sub-role input the coordinator consumes one by one.
// `Short` is the shared input used by Structure / Consistency / Support when
// fewer than 3 transcript chunks are available.
public sealed record DeepDiveSubInputs(
    MainIdeaClarity.MainIdeaClarityInput MainIdeaClarity,
    Structure.StructureInput Structure,
    ConsistencyFocus.ConsistencyFocusInput ConsistencyFocus,
    SupportJustification.SupportJustificationInput SupportJustification,
    LanguageQuality.LanguageQualityInput LanguageQuality,
    EmotionalDelivery.EmotionalDeliveryInput EmotionalDelivery,
    QaHandling.QaHandlingInput QaHandling,
    DeepDiveShortInput Short);

// Per-sub-role completion record: parsed criterion + the underlying
// model turn (or null when the sub-role was skipped programmatically).
public sealed record DeepDiveSubRoleTurn(
    DeepDiveSubRole Role,
    DeepDiveCriterion Criterion,
    long? TurnSequence,
    DateTimeOffset Timestamp,
    Native.BenchmarkSnapshot? Benchmark,
    string? RawResponseJson);

public sealed record DeepDiveAggregateTurnResult(
    IReadOnlyList<DeepDiveSubRoleTurn> SubRoles,
    DeepDiveResult Result,
    DeepDiveSubInputs Inputs);
