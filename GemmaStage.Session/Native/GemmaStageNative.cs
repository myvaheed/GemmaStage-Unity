using System;
using System.Runtime.InteropServices;

namespace GemmaStage.Session.Native;

public static class GemmaStageNative
{
    public static void SetMinLogLevel(int level)
    {
        GemmaStageNativeMethods.SetMinLogLevel(level);
    }

    public static EngineSettingsHandle EngineSettingsCreate(string modelPath, string? mmprojPath = null, string? backend = null)
    {
        Guard.NotNullOrWhiteSpace(modelPath);

        var handle = GemmaStageNativeMethods.EngineSettingsCreate(modelPath, mmprojPath, backend);
        return EnsureHandle(handle, nameof(EngineSettingsCreate));
    }

    public static void EngineSettingsSetContextSize(EngineSettingsHandle settings, int contextSize)
    {
        Guard.NotNull(settings);
        GemmaStageNativeMethods.EngineSettingsSetContextSize(GetHandle(settings), contextSize);
    }

    public static void EngineSettingsSetGpuLayers(EngineSettingsHandle settings, int gpuLayers)
    {
        Guard.NotNull(settings);
        GemmaStageNativeMethods.EngineSettingsSetGpuLayers(GetHandle(settings), gpuLayers);
    }

    public static void EngineSettingsEnableBenchmark(EngineSettingsHandle settings)
    {
        Guard.NotNull(settings);
        GemmaStageNativeMethods.EngineSettingsEnableBenchmark(GetHandle(settings));
    }

    public static EngineHandle EngineCreate(EngineSettingsHandle settings)
    {
        Guard.NotNull(settings);
        var handle = GemmaStageNativeMethods.EngineCreate(GetHandle(settings));
        return EnsureHandle(handle, nameof(EngineCreate));
    }

    public static string EngineGetBackendName(EngineHandle? engine)
    {
        var ptr = GemmaStageNativeMethods.EngineGetBackendName(engine is null ? IntPtr.Zero : GetHandle(engine));
        return Marshal.PtrToStringUTF8(ptr) ?? string.Empty;
    }

    public static ConversationConfigHandle ConversationConfigCreate(
        EngineHandle? engine,
        string? systemMessage,
        string? toolsJson,
        bool enableConstrainedDecoding)
    {
        var handle = GemmaStageNativeMethods.ConversationConfigCreate(
            engine is null ? IntPtr.Zero : GetHandle(engine),
            systemMessage,
            toolsJson,
            enableConstrainedDecoding);

        return EnsureHandle(handle, nameof(ConversationConfigCreate));
    }

    public static void ConversationConfigSetRetainTextOnlyMediaHistory(ConversationConfigHandle config, bool enable)
    {
        Guard.NotNull(config);
        GemmaStageNativeMethods.ConversationConfigSetRetainTextOnlyMediaHistory(GetHandle(config), enable);
    }

    public static void ConversationConfigSetEnableThinking(ConversationConfigHandle config, bool enable)
    {
        Guard.NotNull(config);
        GemmaStageNativeMethods.ConversationConfigSetEnableThinking(GetHandle(config), enable);
    }

    public static ConversationHandle ConversationCreate(EngineHandle engine, ConversationConfigHandle config)
    {
        Guard.NotNull(engine);
        Guard.NotNull(config);

        var handle = GemmaStageNativeMethods.ConversationCreate(GetHandle(engine), GetHandle(config));
        var conversation = EnsureHandle(handle, nameof(ConversationCreate));
        conversation.AttachOwner(engine);
        return conversation;
    }

    public static JsonResponseHandle ConversationSendText(ConversationHandle conversation, string text)
    {
        Guard.NotNull(conversation);
        Guard.NotNullOrWhiteSpace(text);

        var handle = GemmaStageNativeMethods.ConversationSendText(GetHandle(conversation), text);
        return EnsureHandle(handle, nameof(ConversationSendText));
    }

    // Gemma 4 best practice: media is sent before the textual prompt.
    public static JsonResponseHandle ConversationSendAudio(
        ConversationHandle conversation,
        string? promptText,
        byte[] audioData)
    {
        Guard.NotNull(conversation);
        Guard.NotNull(audioData);

        var handle = GemmaStageNativeMethods.ConversationSendAudio(
            GetHandle(conversation),
            promptText,
            audioData,
            checked((nuint)audioData.Length));
        return EnsureHandle(handle, nameof(ConversationSendAudio));
    }

    // Gemma 4 best practice: media is sent before the textual prompt.
    public static JsonResponseHandle ConversationSendImage(
        ConversationHandle conversation,
        string? promptText,
        byte[] imageData)
    {
        Guard.NotNull(conversation);
        Guard.NotNull(imageData);

        var handle = GemmaStageNativeMethods.ConversationSendImage(
            GetHandle(conversation),
            promptText,
            imageData,
            checked((nuint)imageData.Length));
        return EnsureHandle(handle, nameof(ConversationSendImage));
    }

    public static void ConversationCancelProcess(ConversationHandle conversation)
    {
        Guard.NotNull(conversation);
        conversation.CancelProcess();
    }

    public static string? JsonResponseGetString(JsonResponseHandle? response)
    {
        if (response is null || response.IsInvalid)
        {
            return null;
        }

        var ptr = GemmaStageNativeMethods.JsonResponseGetString(GetHandle(response));
        return ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);
    }

    public static BenchmarkInfoHandle ConversationGetBenchmarkInfo(ConversationHandle conversation)
    {
        Guard.NotNull(conversation);
        var handle = GemmaStageNativeMethods.ConversationGetBenchmarkInfo(GetHandle(conversation));
        return EnsureHandle(handle, nameof(ConversationGetBenchmarkInfo));
    }

    public static int ConversationGetKvCacheTokenCount(ConversationHandle conversation)
    {
        Guard.NotNull(conversation);
        return GemmaStageNativeMethods.ConversationGetKvCacheTokenCount(GetHandle(conversation));
    }

    public static int ConversationGetContextSize(ConversationHandle conversation)
    {
        Guard.NotNull(conversation);
        return GemmaStageNativeMethods.ConversationGetContextSize(GetHandle(conversation));
    }

    public static BenchmarkSnapshot GetBenchmarkSnapshot(
        BenchmarkInfoHandle? info,
        ConversationHandle? conversation = null)
    {
        if (info is null || info.IsInvalid)
        {
            return BenchmarkSnapshot.Empty;
        }

        var handle = GetHandle(info);
        var kvCacheTokenCount = 0;
        var contextSize = 0;
        if (conversation is not null && !conversation.IsInvalid && !conversation.IsClosed)
        {
            kvCacheTokenCount = ConversationGetKvCacheTokenCount(conversation);
            contextSize = ConversationGetContextSize(conversation);
        }

        return new BenchmarkSnapshot(
            GemmaStageNativeMethods.BenchmarkInfoGetTimeToFirstToken(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetPrefillTokensPerSec(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetDecodeTokensPerSec(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetPrefillTokenCount(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetDecodeTokenCount(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetCommitOverheadSeconds(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetCommitTokenCount(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetTemplateBuildSeconds(handle),
            GemmaStageNativeMethods.BenchmarkInfoGetPrefillSeconds(handle),
            kvCacheTokenCount,
            contextSize);
    }

    private static THandle EnsureHandle<THandle>(THandle handle, string operation)
        where THandle : SafeHandle
    {
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw new InvalidOperationException($"{operation} returned an invalid native handle.");
        }

        return handle;
    }

    private static nint GetHandle(SafeHandle handle)
    {
        Guard.NotDisposed(handle.IsClosed, handle);
        return handle.DangerousGetHandle();
    }
}

public sealed record BenchmarkSnapshot(
    double TimeToFirstTokenSeconds,
    double PrefillTokensPerSecond,
    double DecodeTokensPerSecond,
    int PrefillTokenCount,
    int DecodeTokenCount,
    double CommitOverheadSeconds,
    int CommitTokenCount,
    double TemplateBuildSeconds,
    double PrefillSeconds,
    int KvCacheTokenCount,
    int ContextSize)
{
    public double KvCachePercent => ContextSize > 0
        ? 100.0 * KvCacheTokenCount / ContextSize
        : 0.0;

    public static BenchmarkSnapshot Empty { get; } = new(
        0.0,
        0.0,
        0.0,
        0,
        0,
        0.0,
        0,
        0.0,
        0.0,
        0,
        0);
}
