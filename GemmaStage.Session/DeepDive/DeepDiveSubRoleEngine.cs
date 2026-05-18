using GemmaStage.Session.Native;
using GemmaStage.Session.Resilience;
using System;
using System.Threading;

namespace GemmaStage.Session.DeepDive;

// Shared engine wiring for the seven per-criterion DeepDive sub-roles. Each
// sub-role builds one of these with its own system prompt and tool schema,
// then calls Evaluate once with a fully-rendered user prompt and a parser
// that turns the model's tool call into the per-role parse result.
public sealed class DeepDiveSubRoleEngine : IDisposable
{
    public sealed record TurnRecord<TParse>(
        long TurnSequence,
        DateTimeOffset Timestamp,
        TParse Parse,
        BenchmarkSnapshot Benchmark,
        string? RawResponseJson);

    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;
    private readonly string _role;
    private readonly bool _enableThinking;

    private long _turnSequence;
    private bool _disposed;

    private DeepDiveSubRoleEngine(
        EngineHandle engine,
        ConversationConfigHandle config,
        Action<string>? warn,
        string role,
        bool enableThinking)
    {
        _engine = engine;
        _config = config;
        _warn = warn;
        _role = role;
        _enableThinking = enableThinking;
    }

    public static DeepDiveSubRoleEngine Create(
        EngineHandle engine,
        string systemPrompt,
        string toolsJson,
        bool enableThinking,
        string role,
        Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        Guard.NotNullOrWhiteSpace(systemPrompt);
        Guard.NotNullOrWhiteSpace(toolsJson);
        Guard.NotNullOrWhiteSpace(role);

        ConversationConfigHandle? config = null;
        try
        {
            config = GemmaStageNative.ConversationConfigCreate(
                engine,
                systemPrompt,
                toolsJson,
                enableConstrainedDecoding: true);
            GemmaStageNative.ConversationConfigSetEnableThinking(config, enableThinking);
        }
        catch
        {
            config?.Dispose();
            throw;
        }

        return new DeepDiveSubRoleEngine(engine, config, warn, role, enableThinking);
    }

    public TurnRecord<TParse> Evaluate<TParse>(string userPrompt, Func<string?, TParse> parse)
        where TParse : class, IToolCallParseResult
    {
        Guard.NotNullOrWhiteSpace(userPrompt);
        Guard.NotNull(parse);
        ThrowIfDisposed();

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parsed = ToolCallRetry.Execute(_role, _config, _enableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendText(conversation, userPrompt);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return parse(responseJson);
        }, message => _warn?.Invoke(message));

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);
        return new TurnRecord<TParse>(turn, timestamp, parsed, benchmark, responseJson);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _config.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }

    private static BenchmarkSnapshot ReadBenchmark(ConversationHandle conversation)
    {
        try
        {
            using var info = GemmaStageNative.ConversationGetBenchmarkInfo(conversation);
            return GemmaStageNative.GetBenchmarkSnapshot(info, conversation);
        }
        catch
        {
            return BenchmarkSnapshot.Empty;
        }
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }
}

// Common parse-result envelope used by every per-sub-role parser.
public sealed record DeepDiveSubRoleParseResult(
    string? ToolName,
    DeepDiveCriterion? Criterion,
    string? Error,
    string? RawAssistantContent) : IToolCallParseResult
{
    public bool IsSuccess => Criterion is not null;
    public bool IsFailure => Error is not null;
}

internal static class DeepDiveCriterionParsing
{
    public static DeepDiveSubRoleParseResult ParseSingleCriterion(
        string? responseJson,
        string expectedToolName,
        bool acceptZero)
    {
        if (string.IsNullOrWhiteSpace(responseJson))
        {
            return new DeepDiveSubRoleParseResult(null, null, "Empty response payload.", null);
        }

        System.Text.Json.JsonDocument document;
        try
        {
            document = System.Text.Json.JsonDocument.Parse(responseJson);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return new DeepDiveSubRoleParseResult(null, null, $"Failed to parse outer JSON: {ex.Message}", responseJson);
        }

        using (document)
        {
            var root = document.RootElement;
            string? rawContent = null;
            if (root.TryGetProperty("content", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                rawContent = c.GetString();
            }

            if (!root.TryGetProperty("tool_calls", out var toolCalls)
                || toolCalls.ValueKind != System.Text.Json.JsonValueKind.Array
                || toolCalls.GetArrayLength() == 0)
            {
                return new DeepDiveSubRoleParseResult(null, null, "Response did not include a tool call.", rawContent);
            }

            var firstCall = toolCalls[0];
            if (!firstCall.TryGetProperty("function", out var function)
                || function.ValueKind != System.Text.Json.JsonValueKind.Object)
            {
                return new DeepDiveSubRoleParseResult(null, null, "First tool call did not contain a function object.", rawContent);
            }

            string? name = null;
            string? argumentsRaw = null;
            if (function.TryGetProperty("name", out var n) && n.ValueKind == System.Text.Json.JsonValueKind.String)
                name = n.GetString();
            if (function.TryGetProperty("arguments", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.String)
                argumentsRaw = a.GetString();

            if (string.IsNullOrEmpty(name))
                return new DeepDiveSubRoleParseResult(null, null, "Tool call had no function name.", rawContent);
            if (name != expectedToolName)
                return new DeepDiveSubRoleParseResult(name, null, $"Unknown tool \"{name}\".", rawContent);
            if (string.IsNullOrEmpty(argumentsRaw))
                return new DeepDiveSubRoleParseResult(name, null, $"Tool call \"{name}\" had no arguments payload.", rawContent);

            System.Text.Json.JsonDocument arguments;
            try
            {
                arguments = System.Text.Json.JsonDocument.Parse(argumentsRaw);
            }
            catch (System.Text.Json.JsonException ex)
            {
                return new DeepDiveSubRoleParseResult(name, null, $"Tool call \"{name}\" arguments were not valid JSON: {ex.Message}", rawContent);
            }

            using (arguments)
            {
                try
                {
                    var args = arguments.RootElement;
                    if (!args.TryGetProperty("verdict", out var verdictEl) || verdictEl.ValueKind != System.Text.Json.JsonValueKind.String)
                        throw new System.Text.Json.JsonException("Required string property \"verdict\" was missing or not a string.");
                    var verdict = verdictEl.GetString() ?? string.Empty;

                    if (!args.TryGetProperty("value", out var valueEl) || valueEl.ValueKind != System.Text.Json.JsonValueKind.Number)
                        throw new System.Text.Json.JsonException("Required integer property \"value\" was missing or not an integer.");
                    if (!valueEl.TryGetInt32(out var v))
                        throw new System.Text.Json.JsonException($"Invalid value {valueEl.GetRawText()}.");

                    var min = acceptZero ? 0 : 1;
                    if (v < min || v > 5)
                        throw new System.Text.Json.JsonException($"Value {v} out of range [{min},5].");

                    return new DeepDiveSubRoleParseResult(name, new DeepDiveCriterion((DeepDiveScore)v, verdict), null, rawContent);
                }
                catch (System.Text.Json.JsonException ex)
                {
                    return new DeepDiveSubRoleParseResult(name, null, $"Tool call \"{name}\" arguments did not satisfy schema: {ex.Message}", rawContent);
                }
            }
        }
    }
}
