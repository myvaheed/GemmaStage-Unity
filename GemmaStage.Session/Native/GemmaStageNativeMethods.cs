using System;
using System.Runtime.InteropServices;

namespace GemmaStage.Session.Native;

internal static class GemmaStageNativeMethods
{
    private const string LibraryName = "GemmaStage";

    [DllImport(LibraryName, EntryPoint = "GS_SetMinLogLevel", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetMinLogLevel(int level);

    [DllImport(LibraryName, EntryPoint = "GS_EngineSettingsCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern EngineSettingsHandle EngineSettingsCreate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string modelPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? mmprojPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? backend);

    [DllImport(LibraryName, EntryPoint = "GS_EngineSettingsDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineSettingsDelete(nint settings);

    [DllImport(LibraryName, EntryPoint = "GS_EngineSettingsSetContextSize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineSettingsSetContextSize(nint settings, int contextSize);

    [DllImport(LibraryName, EntryPoint = "GS_EngineSettingsSetGpuLayers", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineSettingsSetGpuLayers(nint settings, int gpuLayers);

    [DllImport(LibraryName, EntryPoint = "GS_EngineSettingsEnableBenchmark", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineSettingsEnableBenchmark(nint settings);

    [DllImport(LibraryName, EntryPoint = "GS_EngineCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern EngineHandle EngineCreate(nint settings);

    [DllImport(LibraryName, EntryPoint = "GS_EngineDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void EngineDelete(nint engine);

    [DllImport(LibraryName, EntryPoint = "GS_EngineGetBackendName", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint EngineGetBackendName(nint engine);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationConfigCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ConversationConfigHandle ConversationConfigCreate(
        nint engine,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? systemMessage,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? toolsJson,
        [MarshalAs(UnmanagedType.I1)] bool enableConstrainedDecoding);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationConfigDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ConversationConfigDelete(nint config);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationConfigSetRetainTextOnlyMediaHistory", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ConversationConfigSetRetainTextOnlyMediaHistory(
        nint config,
        [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationConfigSetEnableThinking", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ConversationConfigSetEnableThinking(
        nint config,
        [MarshalAs(UnmanagedType.I1)] bool enable);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationCreate", CallingConvention = CallingConvention.Cdecl)]
    internal static extern ConversationHandle ConversationCreate(nint engine, nint config);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ConversationDelete(nint conversation);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationSendText", CallingConvention = CallingConvention.Cdecl)]
    internal static extern JsonResponseHandle ConversationSendText(
        nint conversation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string text);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationSendAudio", CallingConvention = CallingConvention.Cdecl)]
    internal static extern JsonResponseHandle ConversationSendAudio(
        nint conversation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? promptText,
        byte[] audioData,
        nuint audioSize);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationSendImage", CallingConvention = CallingConvention.Cdecl)]
    internal static extern JsonResponseHandle ConversationSendImage(
        nint conversation,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string? promptText,
        byte[] imageData,
        nuint imageSize);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationCancelProcess", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void ConversationCancelProcess(nint conversation);

    [DllImport(LibraryName, EntryPoint = "GS_JsonResponseGetString", CallingConvention = CallingConvention.Cdecl)]
    internal static extern nint JsonResponseGetString(nint response);

    [DllImport(LibraryName, EntryPoint = "GS_JsonResponseDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void JsonResponseDelete(nint response);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationGetBenchmarkInfo", CallingConvention = CallingConvention.Cdecl)]
    internal static extern BenchmarkInfoHandle ConversationGetBenchmarkInfo(nint conversation);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationGetKvCacheTokenCount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ConversationGetKvCacheTokenCount(nint conversation);

    [DllImport(LibraryName, EntryPoint = "GS_ConversationGetContextSize", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int ConversationGetContextSize(nint conversation);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoDelete", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void BenchmarkInfoDelete(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetTimeToFirstToken", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetTimeToFirstToken(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetPrefillTokensPerSec", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetPrefillTokensPerSec(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetDecodeTokensPerSec", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetDecodeTokensPerSec(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetPrefillTokenCount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BenchmarkInfoGetPrefillTokenCount(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetDecodeTokenCount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BenchmarkInfoGetDecodeTokenCount(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetCommitOverheadSeconds", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetCommitOverheadSeconds(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetCommitTokenCount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int BenchmarkInfoGetCommitTokenCount(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetTemplateBuildSeconds", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetTemplateBuildSeconds(nint info);

    [DllImport(LibraryName, EntryPoint = "GS_BenchmarkInfoGetPrefillSeconds", CallingConvention = CallingConvention.Cdecl)]
    internal static extern double BenchmarkInfoGetPrefillSeconds(nint info);
}
