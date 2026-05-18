using System;
using System.Collections.Generic;
using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts.DeepDive;

namespace GemmaStage.Session.DeepDive;

// Orchestrates the seven per-criterion sub-role calls sequentially and
// composes their results into the existing DeepDiveResult shape. Skip
// rules emit (NotApplicable, "<short reason>") programmatically without
// a model call. The Language Quality score is overridden post-parse with
// the deterministic computed value so the published result.json always
// agrees with the rule.
public sealed class DeepDiveCoordinator
{
    private readonly EngineHandle _engine;
    private readonly Action<string>? _warn;
    private readonly Action<DeepDiveSubRole, DeepDiveCriterion>? _onSubRoleCompleted;

    public DeepDiveCoordinator(
        EngineHandle engine,
        Action<DeepDiveSubRole, DeepDiveCriterion>? onSubRoleCompleted = null,
        Action<string>? warn = null)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _warn = warn;
        _onSubRoleCompleted = onSubRoleCompleted;
    }

    public DeepDiveAggregateTurnResult Run(DeepDiveSubInputs inputs)
    {
        Guard.NotNull(inputs);

        var turns = new List<DeepDiveSubRoleTurn>(7);

        var mainIdea = RunMainIdeaClarity(inputs.MainIdeaClarity, turns);
        var structure = RunWithChunkFork(
            DeepDiveSubRole.Structure,
            inputs.Structure.Rows.Count,
            inputs.Structure.Computed.Value,
            StructurePrompts.SystemShort, StructurePrompts.UserTemplateShort,
            StructurePrompts.ToolsJson, Structure.StructureSchemas.ToolName,
            Structure.StructureResponseParser.Parse,
            inputs.Short,
            () => Structure.StructureConversation.Create(_engine, _warn),
            conv => ((Structure.StructureConversation)conv).Evaluate(inputs.Structure),
            turns);
        var consistency = RunWithChunkFork(
            DeepDiveSubRole.ConsistencyFocus,
            inputs.ConsistencyFocus.Rows.Count,
            inputs.ConsistencyFocus.Computed.Value,
            ConsistencyFocusPrompts.SystemShort, ConsistencyFocusPrompts.UserTemplateShort,
            ConsistencyFocusPrompts.ToolsJson, ConsistencyFocus.ConsistencyFocusSchemas.ToolName,
            ConsistencyFocus.ConsistencyFocusResponseParser.Parse,
            inputs.Short,
            () => ConsistencyFocus.ConsistencyFocusConversation.Create(_engine, _warn),
            conv => ((ConsistencyFocus.ConsistencyFocusConversation)conv).Evaluate(inputs.ConsistencyFocus),
            turns);
        var support = RunWithChunkFork(
            DeepDiveSubRole.SupportJustification,
            inputs.SupportJustification.Rows.Count,
            inputs.SupportJustification.Computed.Value,
            SupportJustificationPrompts.SystemShort, SupportJustificationPrompts.UserTemplateShort,
            SupportJustificationPrompts.ToolsJson, SupportJustification.SupportJustificationSchemas.ToolName,
            SupportJustification.SupportJustificationResponseParser.Parse,
            inputs.Short,
            () => SupportJustification.SupportJustificationConversation.Create(_engine, _warn),
            conv => ((SupportJustification.SupportJustificationConversation)conv).Evaluate(inputs.SupportJustification),
            turns);
        var language = RunLanguageQuality(inputs.LanguageQuality, turns);
        var emotional = RunEmotionalDelivery(inputs.EmotionalDelivery, turns);
        var qa = RunQaHandling(inputs.QaHandling, turns);

        var result = new DeepDiveResult(
            mainIdea, structure, consistency, support, language, emotional, qa);
        return new DeepDiveAggregateTurnResult(turns, result, inputs);
    }

    private DeepDiveCriterion RunMainIdeaClarity(
        MainIdeaClarity.MainIdeaClarityInput input,
        List<DeepDiveSubRoleTurn> turns)
    {
        using var conv = input.Anchor is not null
            ? MainIdeaClarity.MainIdeaClarityConversation.CreateWithAnchor(_engine, _warn)
            : MainIdeaClarity.MainIdeaClarityConversation.CreateNoAnchor(_engine, _warn);

        var turn = conv.Evaluate(input);
        return RecordTurn(DeepDiveSubRole.MainIdeaClarity, turn.Parse, turn.TurnSequence, turn.Timestamp, turn.Benchmark, turn.RawResponseJson, turns);
    }

    private DeepDiveCriterion RunLanguageQuality(
        LanguageQuality.LanguageQualityInput input,
        List<DeepDiveSubRoleTurn> turns)
    {
        using var conv = LanguageQuality.LanguageQualityConversation.Create(_engine, _warn);
        var turn = conv.Evaluate(input);
        var criterion = RecordTurn(DeepDiveSubRole.LanguageQuality, turn.Parse, turn.TurnSequence, turn.Timestamp, turn.Benchmark, turn.RawResponseJson, turns);

        // Deterministic override: replace the model's value with the
        // computed score. The model only contributes the verdict prose.
        var computedScore = (DeepDiveScore)input.Computed.value;
        if (criterion.Value != computedScore)
        {
            _warn?.Invoke(
                $"DeepDive language_quality value {(int)criterion.Value} disagreed with the computed value {input.Computed.value} ({input.Computed.label}); overriding.");
            var overridden = criterion with { Value = computedScore };
            // Replace the criterion stored in turns and emit the corrected event.
            var lastIdx = turns.Count - 1;
            turns[lastIdx] = turns[lastIdx] with { Criterion = overridden };
            _onSubRoleCompleted?.Invoke(DeepDiveSubRole.LanguageQuality, overridden);
            return overridden;
        }

        // Already consistent — the per-sub-role event was emitted by RecordTurn.
        return criterion;
    }

    private DeepDiveCriterion RunEmotionalDelivery(
        EmotionalDelivery.EmotionalDeliveryInput input,
        List<DeepDiveSubRoleTurn> turns)
    {
        using var conv = EmotionalDelivery.EmotionalDeliveryConversation.Create(_engine, _warn);
        var turn = conv.Evaluate(input);
        return RecordTurn(DeepDiveSubRole.EmotionalDelivery, turn.Parse, turn.TurnSequence, turn.Timestamp, turn.Benchmark, turn.RawResponseJson, turns);
    }

    private DeepDiveCriterion RunQaHandling(
        QaHandling.QaHandlingInput input,
        List<DeepDiveSubRoleTurn> turns)
    {
        if (input.Spans.Count == 0)
        {
            return RecordSkip(DeepDiveSubRole.QaHandling, "no Q&A rounds occurred", turns);
        }

        using var conv = QaHandling.QaHandlingConversation.Create(_engine, _warn);
        var turn = conv.Evaluate(input);
        return RecordTurn(DeepDiveSubRole.QaHandling, turn.Parse, turn.TurnSequence, turn.Timestamp, turn.Benchmark, turn.RawResponseJson, turns);
    }

    // Shared routing for Structure / Consistency / Support: 0 chunks -> N/A
    // skip; 1-2 chunks -> short prompt against shared DeepDiveShortInput, no
    // override; 3+ chunks -> full conversation with deterministic computed
    // override. The per-criterion specifics (system prompt, tool name, parser,
    // full-branch conversation) are passed in as parameters.
    private DeepDiveCriterion RunWithChunkFork(
        DeepDiveSubRole role,
        int chunkCount,
        int computedValue,
        string shortSystem,
        string shortUserTemplate,
        string toolsJson,
        string toolName,
        Func<string?, DeepDiveSubRoleParseResult> parse,
        DeepDiveShortInput shortInput,
        Func<IDisposable> createFull,
        Func<IDisposable, DeepDiveSubRoleEngine.TurnRecord<DeepDiveSubRoleParseResult>> evaluateFull,
        List<DeepDiveSubRoleTurn> turns)
    {
        if (chunkCount == 0)
        {
            return RecordSkip(role, "no transcript chunks", turns);
        }

        if (chunkCount < 3)
        {
            using var conv = DeepDiveShortConversation.Create(
                _engine, shortSystem, toolsJson, shortUserTemplate, toolName, parse,
                role: $"DeepDive.{role}.Short", warn: _warn);
            var turn = conv.Evaluate(shortInput);
            return RecordTurn(role, turn.Parse, turn.TurnSequence, turn.Timestamp, turn.Benchmark, turn.RawResponseJson, turns);
        }

        using var convFull = createFull();
        var turnFull = evaluateFull(convFull);
        var criterion = RecordTurn(role, turnFull.Parse, turnFull.TurnSequence, turnFull.Timestamp, turnFull.Benchmark, turnFull.RawResponseJson, turns);
        var computedScore = (DeepDiveScore)computedValue;
        if (criterion.Value == computedScore) return criterion;
        _warn?.Invoke($"DeepDive {role} value {(int)criterion.Value} disagreed with computed {computedValue}; overriding.");
        var overridden = criterion with { Value = computedScore };
        turns[turns.Count - 1] = turns[turns.Count - 1] with { Criterion = overridden };
        _onSubRoleCompleted?.Invoke(role, overridden);
        return overridden;
    }

    private DeepDiveCriterion RecordTurn(
        DeepDiveSubRole role,
        DeepDiveSubRoleParseResult parse,
        long turnSequence,
        DateTimeOffset timestamp,
        BenchmarkSnapshot benchmark,
        string? rawJson,
        List<DeepDiveSubRoleTurn> turns)
    {
        DeepDiveCriterion criterion;
        if (parse.IsSuccess && parse.Criterion is { } parsed)
        {
            criterion = parsed;
        }
        else
        {
            _warn?.Invoke($"DeepDive sub-role {role} failed to produce a valid criterion: {parse.Error ?? "(no error)"}");
            criterion = new DeepDiveCriterion(DeepDiveScore.NotApplicable, parse.Error ?? "Sub-role evaluation failed.");
        }

        turns.Add(new DeepDiveSubRoleTurn(role, criterion, turnSequence, timestamp, benchmark, rawJson));
        _onSubRoleCompleted?.Invoke(role, criterion);
        return criterion;
    }

    private DeepDiveCriterion RecordSkip(
        DeepDiveSubRole role,
        string reason,
        List<DeepDiveSubRoleTurn> turns)
    {
        var criterion = new DeepDiveCriterion(DeepDiveScore.NotApplicable, reason);
        turns.Add(new DeepDiveSubRoleTurn(role, criterion, null, DateTimeOffset.UtcNow, null, null));
        _onSubRoleCompleted?.Invoke(role, criterion);
        return criterion;
    }

    public static bool ShouldSkipQa(QaHandling.QaHandlingInput input) => input.Spans.Count == 0;
}
