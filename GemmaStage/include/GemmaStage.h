/*
 * GemmaStage.h — Public C API for GemmaStage.dll (v5, llama.cpp backend)
 *
 * This header defines the complete GS_* export surface consumed by Unity
 * (via P/Invoke) and by native test executables. All types are opaque
 * pointers; lifetime is managed through explicit Create/Delete pairs.
 *
 * Thread safety: individual GS_Conversation* calls are NOT thread-safe.
 *   Use one conversation per thread, or serialise your own calls.
 *   Multiple conversations may share a single GS_Engine safely.
 *
 * Build defines:
 *   GEMMA_STAGE_BUILD  — set when building the DLL (exports symbols)
 *   (absence)          — consumer side (imports symbols)
 */

#ifndef GEMMA_STAGE_H
#define GEMMA_STAGE_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* ── Export / import macro ────────────────────────────────────────────── */

#ifdef _WIN32
#  ifdef GEMMA_STAGE_BUILD
#    define GS_API __declspec(dllexport)
#  else
#    define GS_API __declspec(dllimport)
#  endif
#else
#  define GS_API __attribute__((visibility("default")))
#endif

#ifdef __cplusplus
extern "C" {
#endif

/* ── Opaque handle types ─────────────────────────────────────────────── */

typedef struct GS_Engine             GS_Engine;
typedef struct GS_EngineSettings     GS_EngineSettings;
typedef struct GS_Conversation       GS_Conversation;
typedef struct GS_ConversationConfig GS_ConversationConfig;
typedef struct GS_JsonResponse       GS_JsonResponse;
typedef struct GS_BenchmarkInfo      GS_BenchmarkInfo;

/* ── Callback types ──────────────────────────────────────────────────── */

/** Streaming callback. Invoked once per decoded text chunk.
 *  @param chunk  Null-terminated UTF-8 text.
 *  @param data   Opaque pointer passed to the Send*Stream function.
 */
typedef void (*GS_StreamCallback)(const char* chunk, void* data);

/* ── Logging ─────────────────────────────────────────────────────────── */

/** Set the minimum log level.
 *  0 = DEBUG, 1 = INFO (default), 2 = WARN, 3 = ERROR, 4 = OFF.
 */
GS_API void GS_SetMinLogLevel(int level);

/* ── Engine lifecycle ────────────────────────────────────────────────── */

/** Create engine settings.
 *  @param model_path   Path to the GGUF model file.
 *  @param mmproj_path  Path to the mmproj GGUF (multimodal). NULL to disable.
 *  @param backend      Preferred backend: "cuda", "vulkan", "cpu", or NULL
 *                      (auto-detect: CUDA > Vulkan > CPU).
 */
GS_API GS_EngineSettings* GS_EngineSettingsCreate(
    const char* model_path,
    const char* mmproj_path,
    const char* backend);

GS_API void GS_EngineSettingsDelete(GS_EngineSettings* settings);

/** Set the context size (n_ctx). 0 = use model default. */
GS_API void GS_EngineSettingsSetContextSize(
    GS_EngineSettings* settings, int n_ctx);

/** Set the number of GPU layers to offload. -1 = all layers (default). */
GS_API void GS_EngineSettingsSetGpuLayers(
    GS_EngineSettings* settings, int n_gpu_layers);

/** Enable performance benchmarking. */
GS_API void GS_EngineSettingsEnableBenchmark(GS_EngineSettings* settings);

/** Create the inference engine from settings.
 *  This is a heavy operation: loads model weights, initialises GPU backends.
 *  @return Engine handle, or NULL on failure.
 */
GS_API GS_Engine* GS_EngineCreate(const GS_EngineSettings* settings);

GS_API void GS_EngineDelete(GS_Engine* engine);

/** Query the active backend name (e.g. "CUDA", "Vulkan", "CPU").
 *  The returned pointer is valid for the lifetime of the engine.
 */
GS_API const char* GS_EngineGetBackendName(const GS_Engine* engine);

/* ── Conversation lifecycle ──────────────────────────────────────────── */

/** Create conversation configuration.
 *  @param engine         Engine handle (required for vocabulary access).
 *  @param system_message System prompt text (may be NULL).
 *  @param tools_json     OpenAI-format tool definitions as JSON (may be NULL).
 *  @param enable_constrained_decoding  If true, build GBNF grammar from tools.
 */
GS_API GS_ConversationConfig* GS_ConversationConfigCreate(
    GS_Engine* engine,
    const char* system_message,
    const char* tools_json,
    bool enable_constrained_decoding);

GS_API void GS_ConversationConfigDelete(GS_ConversationConfig* config);

/** When enabled, committed history drops retained media payloads and keeps only
 *  the text portions of prior media turns. This is useful for KV/cache
 *  benchmarking against the default full-history path.
 */
GS_API void GS_ConversationConfigSetRetainTextOnlyMediaHistory(
    GS_ConversationConfig* config,
    bool enable);

/** When enabled, the built-in chat template may inject model thinking tags
 *  and the sampler will honor the template's reasoning control tokens. Disable
 *  this for structured-output roles that should answer directly.
 */
GS_API void GS_ConversationConfigSetEnableThinking(
    GS_ConversationConfig* config,
    bool enable);

/** Override the per-conversation KV cache size (n_ctx).
 *
 *  Default: 0 means inherit GS_Engine::n_ctx (set at engine creation time).
 *  When n_ctx > 0, GS_ConversationCreate uses this value instead.
 *
 *  Use this for short-lived single-purpose conversations whose actual token
 *  usage is far below the engine's window — for example, an ASR conversation
 *  that processes one audio chunk at a time and would otherwise allocate the
 *  full engine KV cache for every chunk.
 *
 *  Intentionally not exposed in the managed wrapper yet; kept in the native
 *  API for future / just-in-case per-role context tuning.
 *
 *  Must be called before GS_ConversationCreate; changing the override on a
 *  config after creation has no effect on the existing conversation.
 */
GS_API void GS_ConversationConfigSetContextSizeOverride(
    GS_ConversationConfig* config,
    int n_ctx);

/** Create a new conversation (llama_context + sampler chain).
 *  Multiple conversations may share the same engine (shared model weights).
 *  @return Conversation handle, or NULL on failure.
 */
GS_API GS_Conversation* GS_ConversationCreate(
    GS_Engine* engine,
    GS_ConversationConfig* config);

GS_API void GS_ConversationDelete(GS_Conversation* conversation);

/* ── Text input ──────────────────────────────────────────────────────── */

GS_API GS_JsonResponse* GS_ConversationSendText(
    GS_Conversation* conversation,
    const char* text);

GS_API int GS_ConversationSendTextStream(
    GS_Conversation* conversation,
    const char* text,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Audio input (blob) ──────────────────────────────────────────────── */

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API GS_JsonResponse* GS_ConversationSendAudio(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* audio_data,
    size_t audio_size);

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API int GS_ConversationSendAudioStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* audio_data,
    size_t audio_size,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Audio input (file) ──────────────────────────────────────────────── */

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API GS_JsonResponse* GS_ConversationSendAudioFromFile(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* audio_path);

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API int GS_ConversationSendAudioFromFileStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* audio_path,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Image input (blob) ──────────────────────────────────────────────── */

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API GS_JsonResponse* GS_ConversationSendImage(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* image_data,
    size_t image_size);

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API int GS_ConversationSendImageStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* image_data,
    size_t image_size,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Image input (file) ──────────────────────────────────────────────── */

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API GS_JsonResponse* GS_ConversationSendImageFromFile(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* image_path);

/** Gemma 4 best practice: media is placed before this prompt text. */
GS_API int GS_ConversationSendImageFromFileStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* image_path,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Raw message (JSON) ──────────────────────────────────────────────── */

GS_API GS_JsonResponse* GS_ConversationSendMessage(
    GS_Conversation* conversation,
    const char* message_json);

GS_API int GS_ConversationSendMessageStream(
    GS_Conversation* conversation,
    const char* message_json,
    GS_StreamCallback callback,
    void* callback_data);

/* ── Cancellation ────────────────────────────────────────────────────── */

/** Cancel any in-progress inference on the conversation.
 *  Safe to call from any thread.
 */
GS_API void GS_ConversationCancelProcess(GS_Conversation* conversation);

/* ── Response access ─────────────────────────────────────────────────── */

GS_API const char* GS_JsonResponseGetString(const GS_JsonResponse* response);
GS_API void        GS_JsonResponseDelete(GS_JsonResponse* response);

/* ── Benchmark info ──────────────────────────────────────────────────── */

GS_API GS_BenchmarkInfo* GS_ConversationGetBenchmarkInfo(
    GS_Conversation* conversation);

GS_API int GS_ConversationGetKvCacheTokenCount(
    const GS_Conversation* conversation);

GS_API int GS_ConversationGetContextSize(
    const GS_Conversation* conversation);

GS_API void   GS_BenchmarkInfoDelete(GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetTimeToFirstToken(const GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetPrefillTokensPerSec(const GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetDecodeTokensPerSec(const GS_BenchmarkInfo* info);
GS_API int    GS_BenchmarkInfoGetPrefillTokenCount(const GS_BenchmarkInfo* info);
GS_API int    GS_BenchmarkInfoGetDecodeTokenCount(const GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetCommitOverheadSeconds(const GS_BenchmarkInfo* info);
GS_API int    GS_BenchmarkInfoGetCommitTokenCount(const GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetTemplateBuildSeconds(const GS_BenchmarkInfo* info);
GS_API double GS_BenchmarkInfoGetPrefillSeconds(const GS_BenchmarkInfo* info);

#ifdef __cplusplus
}  /* extern "C" */
#endif

#endif /* GEMMA_STAGE_H */
