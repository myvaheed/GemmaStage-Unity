using GemmaStage.Session.Native;
using GemmaStage.Session.Prompts;
using GemmaStage.Session.Resilience;
using System;
using System.Threading;

namespace GemmaStage.Session.Perceptor;

public sealed class I2tConversation : IDisposable
{
    private readonly EngineHandle _engine;
    private readonly ConversationConfigHandle _config;
    private readonly Action<string>? _warn;

    private long _turnSequence;
    private bool _disposed;

    public I2tConversation(EngineHandle engine, Action<string>? warn = null)
    {
        Guard.NotNull(engine);
        _engine = engine;
        _warn = warn;

        _config = GemmaStageNative.ConversationConfigCreate(
            engine,
            I2tPrompts.System,
            I2tPrompts.ToolsJson,
            enableConstrainedDecoding: true);
        GemmaStageNative.ConversationConfigSetEnableThinking(_config, I2tPrompts.EnableThinking);
    }

    public PerceptorTurnResult SendImage(byte[] png, string? mainIdeaUnderstanding = null)
    {
        Guard.NotNull(png);
        if (png.Length == 0)
        {
            throw new ArgumentException("Image payload cannot be empty.", nameof(png));
        }
        ThrowIfDisposed();

        var userMessage = I2tPrompts.UserTemplate
            .Replace("{main_idea_understanding}", mainIdeaUnderstanding ?? "unknown");

        string? responseJson = null;
        var benchmark = BenchmarkSnapshot.Empty;

        var parse = ToolCallRetry.Execute("I2t", _config, I2tPrompts.EnableThinking, () =>
        {
            using var slot = _engine.AcquireConversationSlot();
            using var conversation = GemmaStageNative.ConversationCreate(_engine, _config);
            using var response = GemmaStageNative.ConversationSendImage(
                conversation,
                userMessage,
                png);
            responseJson = GemmaStageNative.JsonResponseGetString(response);
            benchmark = ReadBenchmark(conversation);
            return I2tResponseParser.Parse(responseJson);
        }, Warn);

        var timestamp = DateTimeOffset.UtcNow;
        var turn = Interlocked.Increment(ref _turnSequence);

        if (!parse.IsImage || parse.Image is null)
        {
            Warn($"I2T image turn {turn} produced \"{parse.ToolName}\" instead of image observation.");
        }

        return new PerceptorTurnResult(turn, timestamp, null, parse, benchmark, responseJson);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

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

    private void Warn(string message)
    {
        _warn?.Invoke(message);
    }

    private void ThrowIfDisposed()
    {
        Guard.NotDisposed(_disposed, this);
    }
}
