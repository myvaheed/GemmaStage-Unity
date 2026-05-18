/*
 * GemmaStage.cc - public multimodal surface stubs.
 *
 * Current translation-unit layout:
 *
 *   GemmaStageRuntime.cc
 *     - GS_SetMinLogLevel()
 *     - GS_EngineSettingsCreate/Delete()
 *     - GS_EngineSettingsSetContextSize()
 *     - GS_EngineSettingsSetGpuLayers()
 *     - GS_EngineSettingsEnableBenchmark()
 *     - GS_EngineCreate/Delete()
 *     - GS_EngineGetBackendName()
 *     - GS_ConversationConfigCreate/Delete()
 *
 *   GemmaStageConversation.cc
 *     - GS_ConversationCreate/Delete()
 *     - GS_ConversationSendText()
 *     - GS_ConversationSendTextStream()
 *     - GS_ConversationSendMessage()
 *     - GS_ConversationSendMessageStream()
 *     - GS_ConversationCancelProcess()
 *     - GS_JsonResponseGetString/Delete()
 *     - GS_ConversationGetBenchmarkInfo()
 *     - GS_BenchmarkInfoDelete()
 *     - GS_BenchmarkInfoGetTimeToFirstToken()
 *     - GS_BenchmarkInfoGetPrefillTokensPerSec()
 *     - GS_BenchmarkInfoGetDecodeTokensPerSec()
 *     - GS_BenchmarkInfoGetPrefillTokenCount()
 *     - GS_BenchmarkInfoGetDecodeTokenCount()
 *
 *   GemmaStage.cc
 *     - GS_ConversationSendAudio()
 *     - GS_ConversationSendAudioStream()
 *     - GS_ConversationSendAudioFromFile()
 *     - GS_ConversationSendAudioFromFileStream()
 *     - GS_ConversationSendImage()
 *     - GS_ConversationSendImageStream()
 *     - GS_ConversationSendImageFromFile()
 *     - GS_ConversationSendImageFromFileStream()
 *
 * Shared internal structs/helpers used across the split live in
 * GemmaStageInternal.h.
 */

#include "GemmaStageInternal.h"

#include <fstream>

static bool gs_read_file_bytes(
    const char* path,
    std::vector<uint8_t>* bytes)
{
    if (!path || !path[0] || !bytes) {
        return false;
    }

    const std::string normalized = NormalizePath(path);
    std::ifstream input(normalized, std::ios::binary);
    if (!input) {
        gs_log(3, "Failed to open media file: %s", normalized.c_str());
        return false;
    }

    input.seekg(0, std::ios::end);
    const std::streamoff length = input.tellg();
    input.seekg(0, std::ios::beg);
    if (length <= 0) {
        gs_log(3, "Media file is empty: %s", normalized.c_str());
        return false;
    }

    bytes->resize(static_cast<size_t>(length));
    input.read(reinterpret_cast<char*>(bytes->data()),
               static_cast<std::streamsize>(length));
    if (!input) {
        gs_log(3, "Failed to read media file: %s", normalized.c_str());
        bytes->clear();
        return false;
    }

    return true;
}

static std::string gs_build_multimodal_prompt(
    const char* prompt_text)
{
    const std::string marker = mtmd_default_marker();
    // Gemma 4 guidance recommends placing media before the textual prompt.
    std::string       content = marker;

    const char* prompt = prompt_text ? prompt_text : "";
    if (prompt[0]) {
        if (!content.empty()) {
            content += "\n";
        }
        content += prompt;
    }

    if (content.empty()) {
        content = marker;
    }

    return content;
}

static GS_GenerationResult gs_run_audio_turn(
    GS_Conversation* conversation,
    const char* prompt_text,
    std::vector<uint8_t> audio_bytes,
    GS_StreamCallback callback = nullptr,
    void* callback_data = nullptr)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->engine || !conversation->engine->mtmd) {
        gs_log(3, "Audio turn requested without an active mtmd context.");
        return result;
    }

    if (!mtmd_support_audio(conversation->engine->mtmd)) {
        gs_log(3, "Loaded mmproj does not report audio support.");
        return result;
    }

    if (audio_bytes.empty()) {
        gs_log(3, "Audio turn requested with an empty payload.");
        return result;
    }

    return gs_run_user_turn(
        conversation,
        "user",
        gs_build_multimodal_prompt(prompt_text),
        callback,
        callback_data,
        GS_ChatMessage::MediaKind::Audio,
        std::move(audio_bytes));
}

static GS_GenerationResult gs_run_image_turn(
    GS_Conversation* conversation,
    const char* prompt_text,
    std::vector<uint8_t> image_bytes,
    GS_StreamCallback callback = nullptr,
    void* callback_data = nullptr)
{
    GS_GenerationResult result = {};
    if (!conversation || !conversation->engine || !conversation->engine->mtmd) {
        gs_log(3, "Image turn requested without an active mtmd context.");
        return result;
    }

    if (!mtmd_support_vision(conversation->engine->mtmd)) {
        gs_log(3, "Loaded mmproj does not report vision support.");
        return result;
    }

    if (image_bytes.empty()) {
        gs_log(3, "Image turn requested with an empty payload.");
        return result;
    }

    return gs_run_user_turn(
        conversation,
        "user",
        gs_build_multimodal_prompt(prompt_text),
        callback,
        callback_data,
        GS_ChatMessage::MediaKind::Image,
        std::move(image_bytes));
}

extern "C" {

GS_API GS_JsonResponse* GS_ConversationSendAudio(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* audio_data, size_t audio_size)
{
    if (!conversation || !audio_data || audio_size == 0) {
        return nullptr;
    }

    std::vector<uint8_t> audio_bytes(
        static_cast<const uint8_t*>(audio_data),
        static_cast<const uint8_t*>(audio_data) + audio_size);

    GS_GenerationResult result = gs_run_audio_turn(
        conversation, prompt_text, std::move(audio_bytes));
    if (!result.ok) {
        return nullptr;
    }

    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendAudioStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* audio_data, size_t audio_size,
    GS_StreamCallback callback, void* callback_data)
{
    if (!conversation || !audio_data || audio_size == 0) {
        return -1;
    }

    std::vector<uint8_t> audio_bytes(
        static_cast<const uint8_t*>(audio_data),
        static_cast<const uint8_t*>(audio_data) + audio_size);

    GS_GenerationResult result = gs_run_audio_turn(
        conversation, prompt_text, std::move(audio_bytes),
        callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

GS_API GS_JsonResponse* GS_ConversationSendAudioFromFile(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* audio_path)
{
    std::vector<uint8_t> audio_bytes;
    if (!conversation || !gs_read_file_bytes(audio_path, &audio_bytes)) {
        return nullptr;
    }

    GS_GenerationResult result = gs_run_audio_turn(
        conversation, prompt_text, std::move(audio_bytes));
    if (!result.ok) {
        return nullptr;
    }

    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendAudioFromFileStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* audio_path,
    GS_StreamCallback callback, void* callback_data)
{
    std::vector<uint8_t> audio_bytes;
    if (!conversation || !gs_read_file_bytes(audio_path, &audio_bytes)) {
        return -1;
    }

    GS_GenerationResult result = gs_run_audio_turn(
        conversation, prompt_text, std::move(audio_bytes),
        callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

GS_API GS_JsonResponse* GS_ConversationSendImage(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* image_data, size_t image_size)
{
    if (!conversation || !image_data || image_size == 0) {
        return nullptr;
    }

    std::vector<uint8_t> image_bytes(
        static_cast<const uint8_t*>(image_data),
        static_cast<const uint8_t*>(image_data) + image_size);

    GS_GenerationResult result = gs_run_image_turn(
        conversation, prompt_text, std::move(image_bytes));
    if (!result.ok) {
        return nullptr;
    }

    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendImageStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const void* image_data, size_t image_size,
    GS_StreamCallback callback, void* callback_data)
{
    if (!conversation || !image_data || image_size == 0) {
        return -1;
    }

    std::vector<uint8_t> image_bytes(
        static_cast<const uint8_t*>(image_data),
        static_cast<const uint8_t*>(image_data) + image_size);

    GS_GenerationResult result = gs_run_image_turn(
        conversation, prompt_text, std::move(image_bytes),
        callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

GS_API GS_JsonResponse* GS_ConversationSendImageFromFile(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* image_path)
{
    std::vector<uint8_t> image_bytes;
    if (!conversation || !gs_read_file_bytes(image_path, &image_bytes)) {
        return nullptr;
    }

    GS_GenerationResult result = gs_run_image_turn(
        conversation, prompt_text, std::move(image_bytes));
    if (!result.ok) {
        return nullptr;
    }

    return gs_build_turn_response(result);
}

GS_API int GS_ConversationSendImageFromFileStream(
    GS_Conversation* conversation,
    const char* prompt_text,
    const char* image_path,
    GS_StreamCallback callback, void* callback_data)
{
    std::vector<uint8_t> image_bytes;
    if (!conversation || !gs_read_file_bytes(image_path, &image_bytes)) {
        return -1;
    }

    GS_GenerationResult result = gs_run_image_turn(
        conversation, prompt_text, std::move(image_bytes),
        callback, callback_data);
    if (result.ok) {
        return 0;
    }
    return result.cancelled ? 1 : -1;
}

}  // extern "C"
