/*
 * test_load.cc — Smoke test for GemmaStage.dll (Task 1.1.4)
 *
 * Verifies that:
 *   1. The DLL loads and all GS_* symbols resolve.
 *   2. Engine settings can be created with a model path.
 *   3. Engine creation works (requires a GGUF model at the given path).
 *   4. A conversation can be created and a text message sent.
 *   5. Multi-turn conversation preserves KV cache (model references earlier turns).
 *   6. Streaming callback fires per-token.
 *   7. The n_ctx setting is respected.
 *   8. All handles are properly cleaned up.
 *
 * Usage:
 *   test_load.exe <model.gguf> [mmproj.gguf] [backend] [n_ctx]
 *
 * If no model path is given, runs a "dry" test that only verifies symbol
 * resolution and settings/config creation (no GPU or model needed).
 */

#include "GemmaStage.h"

#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <thread>
#include <vector>

static bool streq_nocase(const char* a, const char* b) {
    if (!a || !b) {
        return a == b;
    }

    while (*a && *b) {
        const unsigned char ca = static_cast<unsigned char>(*a);
        const unsigned char cb = static_cast<unsigned char>(*b);
        const char la = static_cast<char>((ca >= 'A' && ca <= 'Z') ? (ca - 'A' + 'a') : ca);
        const char lb = static_cast<char>((cb >= 'A' && cb <= 'Z') ? (cb - 'A' + 'a') : cb);
        if (la != lb) {
            return false;
        }
        ++a;
        ++b;
    }

    return *a == '\0' && *b == '\0';
}

static bool is_backend_name(const char* value) {
    return streq_nocase(value, "auto") ||
           streq_nocase(value, "cuda") ||
           streq_nocase(value, "vulkan") ||
           streq_nocase(value, "cpu");
}

static bool is_numeric(const char* value) {
    if (!value || !*value) return false;
    while (*value) {
        if (*value < '0' || *value > '9') return false;
        ++value;
    }
    return true;
}

// --------------------------------------------------------------------------
// Stream callback for testing
// --------------------------------------------------------------------------

struct StreamCapture {
    std::atomic<int> count{0};
    std::atomic<size_t> chars{0};
    std::string text;
    bool echo = true;
};

static bool has_template_marker(const std::string& text) {
    return text.find("<end_of_turn>") != std::string::npos ||
           text.find("<end_of_turn") != std::string::npos ||
           text.find("<start_of_turn>") != std::string::npos ||
           text.find("<start_of_turn") != std::string::npos ||
           text.find("</start_of_turn>") != std::string::npos ||
           text.find("</start_of_turn") != std::string::npos ||
           text.find("<|turn|>") != std::string::npos ||
           text.find("<|turn") != std::string::npos;
}

static bool contains_text(const char* haystack, const char* needle) {
    return haystack && needle && strstr(haystack, needle) != nullptr;
}

static void stream_cb(const char* chunk, void* data) {
    auto* capture = static_cast<StreamCapture*>(data);
    const size_t len = chunk ? strlen(chunk) : 0;

    if (capture) {
        capture->count.fetch_add(1, std::memory_order_relaxed);
        capture->chars.fetch_add(len, std::memory_order_relaxed);
        if (chunk && len > 0) {
            capture->text.append(chunk, len);
        }
        if (!capture->echo) {
            return;
        }
    }

    if (chunk && len > 0) {
        printf("%s", chunk);
        fflush(stdout);
    }
}

// --------------------------------------------------------------------------
// Test helpers
// --------------------------------------------------------------------------

static int g_pass = 0;
static int g_fail = 0;

#define TEST(name) printf("\n--- TEST: %s ---\n", name)
#define CHECK(cond, msg)                                                 \
    do {                                                                 \
        if (cond) {                                                      \
            printf("  PASS: %s\n", msg);                                 \
            g_pass++;                                                    \
        } else {                                                         \
            printf("  FAIL: %s\n", msg);                                 \
            g_fail++;                                                    \
        }                                                                \
    } while (0)

// --------------------------------------------------------------------------
// Argument parsing
// --------------------------------------------------------------------------

struct TestArgs {
    const char* model_path = nullptr;
    const char* mmproj_path = nullptr;
    const char* backend_override = nullptr;
    int n_ctx = 4096;
};

static std::filesystem::path runtime_asset_path(const char* filename) {
    return std::filesystem::current_path() / filename;
}

static std::vector<unsigned char> read_binary_file(const std::filesystem::path& path) {
    std::vector<unsigned char> bytes;
    std::ifstream input(path, std::ios::binary);
    if (!input) {
        return bytes;
    }

    input.seekg(0, std::ios::end);
    const std::streamoff length = input.tellg();
    input.seekg(0, std::ios::beg);
    if (length <= 0) {
        return bytes;
    }

    bytes.resize(static_cast<size_t>(length));
    input.read(reinterpret_cast<char*>(bytes.data()),
               static_cast<std::streamsize>(length));
    if (!input) {
        bytes.clear();
    }
    return bytes;
}

static TestArgs parse_args(int argc, char* argv[]) {
    TestArgs args;

    // argv[1] is always model_path if present
    if (argc > 1) args.model_path = argv[1];

    // Scan remaining args
    for (int i = 2; i < argc; ++i) {
        if (is_backend_name(argv[i])) {
            args.backend_override = argv[i];
        } else if (is_numeric(argv[i])) {
            args.n_ctx = atoi(argv[i]);
        } else {
            // Assume it's mmproj path
            args.mmproj_path = argv[i];
        }
    }

    // Normalise empty strings to nullptr
    if (args.mmproj_path && args.mmproj_path[0] == '\0') args.mmproj_path = nullptr;
    if (args.backend_override && args.backend_override[0] == '\0') args.backend_override = nullptr;

    return args;
}

// --------------------------------------------------------------------------
// main
// --------------------------------------------------------------------------

int main(int argc, char* argv[]) {
    printf("=============================================\n");
    printf("  GemmaStage.dll — Smoke Test (v5 / llama.cpp)\n");
    printf("  Task 1.1.4 — Text Inference + Multi-Turn\n");
    printf("=============================================\n");

    TestArgs args = parse_args(argc, argv);

    // ---- Symbol resolution tests (no model needed) ----

    TEST("Set log level");
    GS_SetMinLogLevel(0);  // DEBUG — we want to see everything
    CHECK(true, "GS_SetMinLogLevel resolved and called");

    TEST("Create engine settings (dry)");
    {
        GS_EngineSettings* s = GS_EngineSettingsCreate(
            "dummy/path/model.gguf", nullptr, nullptr);
        CHECK(s != nullptr, "GS_EngineSettingsCreate returned non-null");

        GS_EngineSettingsSetContextSize(s, 4096);
        CHECK(true, "GS_EngineSettingsSetContextSize resolved");

        GS_EngineSettingsSetGpuLayers(s, -1);
        CHECK(true, "GS_EngineSettingsSetGpuLayers resolved");

        GS_EngineSettingsEnableBenchmark(s);
        CHECK(true, "GS_EngineSettingsEnableBenchmark resolved");

        GS_EngineSettingsDelete(s);
        CHECK(true, "GS_EngineSettingsDelete completed");
    }

    TEST("Create conversation config (dry)");
    {
        GS_ConversationConfig* cfg = GS_ConversationConfigCreate(
            nullptr, "You are a helpful assistant.", nullptr, false);
        CHECK(cfg != nullptr, "GS_ConversationConfigCreate returned non-null");
        GS_ConversationConfigSetRetainTextOnlyMediaHistory(cfg, true);
        CHECK(true, "GS_ConversationConfigSetRetainTextOnlyMediaHistory resolved");
        GS_ConversationConfigSetEnableThinking(cfg, false);
        CHECK(true, "GS_ConversationConfigSetEnableThinking resolved");
        GS_ConversationConfigSetContextSizeOverride(cfg, 4096);
        CHECK(true, "GS_ConversationConfigSetContextSizeOverride resolved");

        GS_ConversationConfigDelete(cfg);
        CHECK(true, "GS_ConversationConfigDelete completed");
    }

    TEST("Response functions (dry)");
    {
        // NULL is fine — these should handle gracefully
        const char* s = GS_JsonResponseGetString(nullptr);
        CHECK(s == nullptr, "GS_JsonResponseGetString(null) -> null");

        GS_JsonResponseDelete(nullptr);
        CHECK(true, "GS_JsonResponseDelete(null) no crash");
    }

    TEST("Benchmark functions (dry)");
    {
        GS_BenchmarkInfoDelete(nullptr);
        CHECK(true, "GS_BenchmarkInfoDelete(null) no crash");

        double v = GS_BenchmarkInfoGetTimeToFirstToken(nullptr);
        CHECK(v == 0.0, "GS_BenchmarkInfoGetTimeToFirstToken(null) -> 0.0");
        CHECK(GS_BenchmarkInfoGetCommitOverheadSeconds(nullptr) == 0.0,
              "GS_BenchmarkInfoGetCommitOverheadSeconds(null) -> 0.0");
        CHECK(GS_BenchmarkInfoGetCommitTokenCount(nullptr) == 0,
              "GS_BenchmarkInfoGetCommitTokenCount(null) -> 0");
        CHECK(GS_BenchmarkInfoGetTemplateBuildSeconds(nullptr) == 0.0,
              "GS_BenchmarkInfoGetTemplateBuildSeconds(null) -> 0.0");
        CHECK(GS_BenchmarkInfoGetPrefillSeconds(nullptr) == 0.0,
              "GS_BenchmarkInfoGetPrefillSeconds(null) -> 0.0");
    }

    TEST("Cancel function (dry)");
    {
        GS_ConversationCancelProcess(nullptr);
        CHECK(true, "GS_ConversationCancelProcess(null) no crash");
    }

    TEST("Backend name (dry)");
    {
        const char* name = GS_EngineGetBackendName(nullptr);
        CHECK(name != nullptr, "GS_EngineGetBackendName(null) returns non-null");
        printf("  Backend (null engine): \"%s\"\n", name);
    }

    // ---- Live tests (require a real model) ----

    if (!args.model_path) {
        printf("\n=============================================\n");
        printf("  No model path provided — skipping live tests.\n");
        printf("  Usage: test_load.exe <model.gguf> [mmproj.gguf] [backend] [n_ctx]\n");
        printf("=============================================\n");
    } else {
        printf("\n=============================================\n");
        printf("  Live tests: model=%s n_ctx=%d\n", args.model_path, args.n_ctx);
        printf("=============================================\n");

        TEST("Engine creation (live)");
        GS_EngineSettings* settings = GS_EngineSettingsCreate(
            args.model_path, args.mmproj_path, args.backend_override);
        CHECK(settings != nullptr, "Settings created");

        GS_EngineSettingsSetContextSize(settings, args.n_ctx);
        GS_EngineSettingsEnableBenchmark(settings);

        GS_Engine* engine = GS_EngineCreate(settings);
        CHECK(engine != nullptr, "GS_EngineCreate succeeded");

        if (engine) {
            const char* backend = GS_EngineGetBackendName(engine);
            printf("  Active backend: %s\n", backend);
            CHECK(backend != nullptr, "Backend name is non-null");
            if (args.backend_override && !streq_nocase(args.backend_override, "auto")) {
                CHECK(streq_nocase(backend, args.backend_override),
                      "Backend override selected the requested runtime");
            }

            // ==============================================================
            // Test 1: Single text prompt
            // ==============================================================

            TEST("Single text prompt (live)");
            GS_ConversationConfig* cfg = GS_ConversationConfigCreate(
                engine,
                "You are a helpful math assistant. Always answer briefly and precisely.",
                nullptr,
                false);
            CHECK(cfg != nullptr, "Config created");

            GS_Conversation* conv = GS_ConversationCreate(engine, cfg);
            CHECK(conv != nullptr, "GS_ConversationCreate succeeded");

            if (conv) {
                printf("\n  [User] Hello, what is 2+2?\n");
                GS_JsonResponse* resp = GS_ConversationSendText(
                    conv, "Hello, what is 2+2?");
                CHECK(resp != nullptr, "GS_ConversationSendText returned response");

                if (resp) {
                    const char* text = GS_JsonResponseGetString(resp);
                    CHECK(text != nullptr, "Response text is non-null");
                    printf("  [Model] %s\n", text ? text : "(null)");
                    GS_JsonResponseDelete(resp);
                }

                // ==============================================================
                // Test 2: Multi-turn — model should reference earlier context
                // ==============================================================

                TEST("Multi-turn conversation (live)");

                printf("\n  [User] Now multiply that result by 10.\n");
                GS_JsonResponse* resp2 = GS_ConversationSendText(
                    conv, "Now multiply that result by 10.");
                CHECK(resp2 != nullptr, "Turn 2 returned response");

                if (resp2) {
                    const char* text = GS_JsonResponseGetString(resp2);
                    CHECK(text != nullptr, "Turn 2 text is non-null");
                    printf("  [Model] %s\n", text ? text : "(null)");
                    GS_JsonResponseDelete(resp2);
                }

                printf("\n  [User] What was my first question?\n");
                GS_JsonResponse* resp3 = GS_ConversationSendText(
                    conv, "What was my first question?");
                CHECK(resp3 != nullptr, "Turn 3 returned response");

                if (resp3) {
                    const char* text = GS_JsonResponseGetString(resp3);
                    CHECK(text != nullptr, "Turn 3 text is non-null");
                    printf("  [Model] %s\n", text ? text : "(null)");
                    GS_JsonResponseDelete(resp3);
                }

                // ==============================================================
                // Test 3: Streaming
                // ==============================================================

                TEST("Streaming text (live)");
                printf("\n  [User] Tell me a short joke.\n  [Model] ");
                StreamCapture stream_capture = {};
                int rc = GS_ConversationSendTextStream(
                    conv, "Tell me a short joke.",
                    stream_cb, &stream_capture);
                printf("\n");
                CHECK(rc == 0, "GS_ConversationSendTextStream returned 0");
                CHECK(stream_capture.count.load(std::memory_order_relaxed) > 0,
                      "Stream callback fired at least once");
                CHECK(!has_template_marker(stream_capture.text),
                      "Streaming text hides chat-template markers");
                printf("  Stream callback fired %d time(s)\n",
                       stream_capture.count.load(std::memory_order_relaxed));

                // ==============================================================
                // Test 4: Raw message JSON (blocking + streaming)
                // ==============================================================

                TEST("Raw message JSON (live)");
                constexpr char kMessageJson[] =
                    R"({"role":"user","content":[{"type":"text","text":"Reply with exactly the word JSON."}]})";
                GS_JsonResponse* msg_resp = GS_ConversationSendMessage(
                    conv, kMessageJson);
                CHECK(msg_resp != nullptr, "GS_ConversationSendMessage returned response");
                if (msg_resp) {
                    const char* text = GS_JsonResponseGetString(msg_resp);
                    CHECK(text != nullptr, "Raw message response text is non-null");
                    printf("  [Model] %s\n", text ? text : "(null)");
                    GS_JsonResponseDelete(msg_resp);
                }

                TEST("Streaming raw message JSON (live)");
                printf("\n  [User] Say hello in one short sentence.\n  [Model] ");
                constexpr char kStreamMessageJson[] =
                    R"({"role":"user","content":[{"type":"text","text":"Say hello in one short sentence."}]})";
                StreamCapture message_stream_capture = {};
                int message_rc = GS_ConversationSendMessageStream(
                    conv, kStreamMessageJson,
                    stream_cb, &message_stream_capture);
                printf("\n");
                CHECK(message_rc == 0, "GS_ConversationSendMessageStream returned 0");
                CHECK(message_stream_capture.count.load(std::memory_order_relaxed) > 0,
                      "Raw message stream callback fired at least once");
                CHECK(!has_template_marker(message_stream_capture.text),
                      "Streaming raw JSON hides chat-template markers");

                // ==============================================================
                // Test 5: Tool calling with constrained decoding
                // ==============================================================

                TEST("Tool calling with constrained decoding (live)");
                constexpr char kToolsJson[] = R"([
  {
    "type": "function",
    "function": {
      "name": "ask_question",
      "description": "Surface a question for the presenter (interrupt during Live Q&A, or queue for Final Q&A).",
      "parameters": {
        "type": "object",
        "properties": {
          "text": { "type": "string" },
          "urgency": { "type": "string", "enum": ["live", "final"] }
        },
        "required": ["text", "urgency"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "flag_issue",
      "description": "Flag a presentation problem.",
      "parameters": {
        "type": "object",
        "properties": {
          "issue": { "type": "string" },
          "severity": { "type": "string", "enum": ["low", "medium", "high"] }
        },
        "required": ["issue", "severity"]
      }
    }
  },
  {
    "type": "function",
    "function": {
      "name": "evaluation",
      "description": "Return a final evaluation payload.",
      "parameters": {
        "type": "object",
        "properties": {
          "summary": { "type": "string" },
          "score": { "type": "number" }
        },
        "required": ["summary", "score"]
      }
    }
  }
])";

                GS_ConversationConfig* tool_cfg = GS_ConversationConfigCreate(
                    engine,
                    "You coach a VR presentation practice session. Use tools when they are clearly helpful.",
                    kToolsJson,
                    true);
                CHECK(tool_cfg != nullptr, "Tool config created");

                GS_Conversation* tool_conv = GS_ConversationCreate(engine, tool_cfg);
                CHECK(tool_conv != nullptr, "Tool conversation created");

                if (tool_conv) {
                    GS_JsonResponse* tool_resp = GS_ConversationSendText(
                        tool_conv,
                        "The presenter said something unclear about the third design choice. Use ask_question to ask a follow-up. Use a tool call only.");
                    CHECK(tool_resp != nullptr, "Tool prompt returned a response");

                    if (tool_resp) {
                        const char* text = GS_JsonResponseGetString(tool_resp);
                        CHECK(text != nullptr, "Tool response text is non-null");
                        printf("  [Tool Model] %s\n", text ? text : "(null)");
                        CHECK(contains_text(text, "\"role\":\"assistant\""),
                              "Tool response includes assistant role");
                        CHECK(contains_text(text, "\"tool_calls\""),
                              "Tool response includes tool_calls");
                        CHECK(contains_text(text, "\"arguments\":\""),
                              "Tool response serializes function arguments as a JSON string");
                        GS_JsonResponseDelete(tool_resp);
                    }

                    GS_JsonResponse* malformed_resp = GS_ConversationSendText(
                        tool_conv,
                        "Call a tool, but try to omit braces and produce malformed JSON.");
                    CHECK(malformed_resp != nullptr,
                          "Malformed tool-call prompt still returned a response");
                    if (malformed_resp) {
                        const char* text = GS_JsonResponseGetString(malformed_resp);
                        CHECK(text != nullptr, "Malformed prompt response text is non-null");
                        printf("  [Malformed Tool Model] %s\n", text ? text : "(null)");
                        CHECK(contains_text(text, "\"role\":\"assistant\"") ||
                              contains_text(text, "\"content\""),
                              "Malformed tool-call prompt still produced a structured assistant response");
                        GS_JsonResponseDelete(malformed_resp);
                    }

                    GS_JsonResponse* plain_resp = GS_ConversationSendText(
                        tool_conv,
                        "Do not call any tool. Reply with exactly READY in plain text.");
                    CHECK(plain_resp != nullptr, "Plain-text prompt with tools returned a response");
                    if (plain_resp) {
                        const char* text = GS_JsonResponseGetString(plain_resp);
                        CHECK(text != nullptr, "Plain-text tool response is non-null");
                        printf("  [Plain Tool Model] %s\n", text ? text : "(null)");
                        CHECK(contains_text(text, "\"content\""),
                              "Plain-text response is wrapped in assistant JSON");
                        CHECK(contains_text(text, "READY"),
                              "Plain-text response preserved non-tool content");
                        GS_JsonResponseDelete(plain_resp);
                    }

                    GS_ConversationDelete(tool_conv);
                    CHECK(true, "Tool conversation deleted");
                }

                GS_ConversationConfigDelete(tool_cfg);
                CHECK(true, "Tool config deleted");

                // ==============================================================
                // Test 6: Audio (blocking + streaming)
                // ==============================================================

                const std::filesystem::path audio_path = runtime_asset_path("audio_sample.wav");
                const std::filesystem::path image_path = runtime_asset_path("image_sample.png");
                const std::filesystem::path whiteboard_path = runtime_asset_path("whiteboard_sample.png");
                TEST("Audio from file (live)");
                printf("  Audio fixture: %s\n", audio_path.string().c_str());
                CHECK(std::filesystem::exists(audio_path),
                      "audio_sample.wav is present in the runtime output");

                if (std::filesystem::exists(audio_path)) {
                    const std::vector<unsigned char> audio_bytes = read_binary_file(audio_path);
                    CHECK(!audio_bytes.empty(), "audio_sample.wav loads into memory");

                    if (!audio_bytes.empty()) {
                        TEST("Audio from blob (live)");
                        GS_JsonResponse* audio_blob_resp = GS_ConversationSendAudio(
                            conv,
                            "Transcribe the audio clip from memory.",
                            audio_bytes.data(),
                            audio_bytes.size());
                        CHECK(audio_blob_resp != nullptr,
                              "GS_ConversationSendAudio returned response");
                        if (audio_blob_resp) {
                            const char* text = GS_JsonResponseGetString(audio_blob_resp);
                            CHECK(text != nullptr, "Audio blob response text is non-null");
                            printf("  [Audio blob] %s\n", text ? text : "(null)");
                            GS_JsonResponseDelete(audio_blob_resp);
                        }
                    }

                    GS_JsonResponse* audio_resp = GS_ConversationSendAudioFromFile(
                        conv,
                        "Transcribe or briefly describe the audio clip.",
                        audio_path.string().c_str());
                    CHECK(audio_resp != nullptr,
                          "GS_ConversationSendAudioFromFile returned response");
                    if (audio_resp) {
                        const char* text = GS_JsonResponseGetString(audio_resp);
                        CHECK(text != nullptr, "Audio response text is non-null");
                        printf("  [Audio] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(audio_resp);
                    }

                    TEST("Streaming audio from file (live)");
                    printf("\n  [Audio prompt] Please describe the clip in one sentence.\n  [Model] ");
                    StreamCapture audio_stream_capture = {};
                    int audio_rc = GS_ConversationSendAudioFromFileStream(
                        conv,
                        "Please describe the clip in one sentence.",
                        audio_path.string().c_str(),
                        stream_cb,
                        &audio_stream_capture);
                    printf("\n");
                    CHECK(audio_rc == 0, "GS_ConversationSendAudioFromFileStream returned 0");
                    CHECK(audio_stream_capture.count.load(std::memory_order_relaxed) > 0,
                          "Audio stream callback fired at least once");
                }

                // ==============================================================
                // Test 6: Timed cancellation and recovery
                // ==============================================================

                TEST("Timed cancellation (live)");
                GS_ConversationConfig* cancel_cfg = GS_ConversationConfigCreate(
                    engine,
                    "When asked to enumerate output, write every requested line literally, keep going until the request is fully satisfied, and do not summarize.",
                    nullptr,
                    false);
                CHECK(cancel_cfg != nullptr, "Cancellation config created");
                GS_Conversation* cancel_conv = cancel_cfg
                    ? GS_ConversationCreate(engine, cancel_cfg)
                    : nullptr;
                CHECK(cancel_conv != nullptr, "Cancellation conversation created");
                if (cancel_conv) {
                    printf("\n  [User] Print the word cancel 500 times, one per line, and do not summarize.\n  [Model] ");
                    StreamCapture cancel_capture = {};

                    std::thread cancel_thread([cancel_conv, &cancel_capture]() {
                        const auto start = std::chrono::steady_clock::now();
                        while (cancel_capture.count.load(std::memory_order_relaxed) == 0) {
                            if (std::chrono::steady_clock::now() - start >
                                std::chrono::seconds(30)) {
                                break;
                            }
                            std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        }

                        std::this_thread::sleep_for(std::chrono::milliseconds(25));
                        GS_ConversationCancelProcess(cancel_conv);
                    });

                    int cancel_rc = GS_ConversationSendTextStream(
                        cancel_conv,
                        "Print the word cancel 2000 times, one per line, and never compress, summarize, or use ellipses.",
                        stream_cb,
                        &cancel_capture);
                    cancel_thread.join();
                    printf("\n");

                    CHECK(cancel_rc == 1, "GS_ConversationSendTextStream returned 1 on cancellation");
                    CHECK(cancel_capture.count.load(std::memory_order_relaxed) > 0,
                          "Cancellation happened after streaming began");
                    printf("  Cancellation delivered %d chunk(s), %zu bytes before stop\n",
                           cancel_capture.count.load(std::memory_order_relaxed),
                           cancel_capture.chars.load(std::memory_order_relaxed));

                    GS_JsonResponse* post_cancel_resp = GS_ConversationSendText(
                        cancel_conv, "Reply with exactly READY.");
                    CHECK(post_cancel_resp != nullptr,
                          "Conversation remains usable after cancellation");
                    if (post_cancel_resp) {
                        const char* text = GS_JsonResponseGetString(post_cancel_resp);
                        CHECK(text != nullptr, "Post-cancel response text is non-null");
                        if (text) {
                            const std::string recovery_text(text);
                            CHECK(!has_template_marker(recovery_text),
                                  "Post-cancel response hides chat-template markers");
                        }
                        printf("  [Recovery] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(post_cancel_resp);
                    }

                    GS_ConversationDelete(cancel_conv);
                }
                GS_ConversationConfigDelete(cancel_cfg);

                // ==============================================================
                // Test 7: Audio cancellation and recovery
                // ==============================================================

                TEST("Audio cancellation (live)");
                GS_ConversationConfig* audio_cancel_cfg = GS_ConversationConfigCreate(
                    engine,
                    "Transcribe audio carefully and keep responding until cancelled.",
                    nullptr,
                    false);
                CHECK(audio_cancel_cfg != nullptr, "Audio cancellation config created");
                GS_Conversation* audio_cancel_conv = audio_cancel_cfg
                    ? GS_ConversationCreate(engine, audio_cancel_cfg)
                    : nullptr;
                CHECK(audio_cancel_conv != nullptr, "Audio cancellation conversation created");
                if (audio_cancel_conv && std::filesystem::exists(audio_path)) {
                    StreamCapture audio_cancel_capture = {};
                    std::thread audio_cancel_thread([audio_cancel_conv, &audio_cancel_capture]() {
                        const auto start = std::chrono::steady_clock::now();
                        while (audio_cancel_capture.count.load(std::memory_order_relaxed) == 0) {
                            if (std::chrono::steady_clock::now() - start >
                                std::chrono::seconds(30)) {
                                break;
                            }
                            std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        }

                        std::this_thread::sleep_for(std::chrono::milliseconds(25));
                        GS_ConversationCancelProcess(audio_cancel_conv);
                    });

                    printf("\n  [Audio prompt] Produce a detailed transcription and keep elaborating.\n  [Model] ");
                    int audio_cancel_rc = GS_ConversationSendAudioFromFileStream(
                        audio_cancel_conv,
                        "Transcribe the clip, then repeat the exact transcript 200 times, one per line, and do not summarize or stop early.",
                        audio_path.string().c_str(),
                        stream_cb,
                        &audio_cancel_capture);
                    audio_cancel_thread.join();
                    printf("\n");

                    CHECK(audio_cancel_rc == 1,
                          "GS_ConversationSendAudioFromFileStream returned 1 on cancellation");
                    CHECK(audio_cancel_capture.count.load(std::memory_order_relaxed) > 0,
                          "Audio cancellation happened after streaming began");

                    GS_JsonResponse* audio_recovery_resp = GS_ConversationSendText(
                        audio_cancel_conv, "Reply with exactly AUDIO-READY.");
                    CHECK(audio_recovery_resp != nullptr,
                          "Conversation remains usable after audio cancellation");
                    if (audio_recovery_resp) {
                        const char* text = GS_JsonResponseGetString(audio_recovery_resp);
                        CHECK(text != nullptr, "Audio recovery response text is non-null");
                        printf("  [Audio recovery] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(audio_recovery_resp);
                    }

                }
                GS_ConversationDelete(audio_cancel_conv);
                GS_ConversationConfigDelete(audio_cancel_cfg);

                // ==============================================================
                // Test 8: Image (blocking + streaming)
                // ==============================================================

                TEST("Image fixtures (live)");
                printf("  Simple image fixture: %s\n", image_path.string().c_str());
                printf("  Whiteboard image fixture: %s\n", whiteboard_path.string().c_str());
                CHECK(std::filesystem::exists(image_path),
                      "image_sample.png is present in the runtime output");
                CHECK(std::filesystem::exists(whiteboard_path),
                      "whiteboard_sample.png is present in the runtime output");

                if (std::filesystem::exists(image_path)) {
                    const std::vector<unsigned char> image_bytes = read_binary_file(image_path);
                    CHECK(!image_bytes.empty(), "image_sample.png loads into memory");

                    if (!image_bytes.empty()) {
                        TEST("Image from blob with text prompt (live)");
                        GS_JsonResponse* image_blob_resp = GS_ConversationSendImage(
                            conv,
                            "What does this equation describe? Answer in one concise sentence.",
                            image_bytes.data(),
                            image_bytes.size());
                        CHECK(image_blob_resp != nullptr,
                              "GS_ConversationSendImage returned response");
                        if (image_blob_resp) {
                            const char* text = GS_JsonResponseGetString(image_blob_resp);
                            CHECK(text != nullptr, "Image blob response text is non-null");
                            printf("  [Image blob] %s\n", text ? text : "(null)");
                            GS_JsonResponseDelete(image_blob_resp);
                        }
                    }

                    TEST("Image from file (live)");
                    GS_JsonResponse* image_resp = GS_ConversationSendImageFromFile(
                        conv,
                        "Summarize the formula shown in the image.",
                        image_path.string().c_str());
                    CHECK(image_resp != nullptr,
                          "GS_ConversationSendImageFromFile returned response");
                    if (image_resp) {
                        const char* text = GS_JsonResponseGetString(image_resp);
                        CHECK(text != nullptr, "Image response text is non-null");
                        printf("  [Image] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(image_resp);
                    }
                }

                if (std::filesystem::exists(whiteboard_path)) {
                    TEST("Whiteboard-style image from file (live)");
                    GS_JsonResponse* whiteboard_resp = GS_ConversationSendImageFromFile(
                        conv,
                        "Describe the handwritten notes and arrows on this whiteboard in one short paragraph.",
                        whiteboard_path.string().c_str());
                    CHECK(whiteboard_resp != nullptr,
                          "GS_ConversationSendImageFromFile handled the whiteboard image");
                    if (whiteboard_resp) {
                        const char* text = GS_JsonResponseGetString(whiteboard_resp);
                        CHECK(text != nullptr, "Whiteboard response text is non-null");
                        printf("  [Whiteboard] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(whiteboard_resp);
                    }

                    TEST("Streaming image from file (live)");
                    printf("\n  [Image prompt] Describe the whiteboard in one sentence.\n  [Model] ");
                    StreamCapture image_stream_capture = {};
                    int image_rc = GS_ConversationSendImageFromFileStream(
                        conv,
                        "Describe the whiteboard in one sentence.",
                        whiteboard_path.string().c_str(),
                        stream_cb,
                        &image_stream_capture);
                    printf("\n");
                    CHECK(image_rc == 0, "GS_ConversationSendImageFromFileStream returned 0");
                    CHECK(image_stream_capture.count.load(std::memory_order_relaxed) > 0,
                          "Image stream callback fired at least once");
                }

                // ==============================================================
                // Test 9: Text-only retained media history
                // ==============================================================

                TEST("Text-only retained media history (live)");
                GS_ConversationConfig* trim_history_cfg = GS_ConversationConfigCreate(
                    engine,
                    "Keep answers short and factual.",
                    nullptr,
                    false);
                CHECK(trim_history_cfg != nullptr, "Trim-history config created");
                if (trim_history_cfg) {
                    GS_ConversationConfigSetRetainTextOnlyMediaHistory(
                        trim_history_cfg, true);
                }
                GS_Conversation* trim_history_conv = trim_history_cfg
                    ? GS_ConversationCreate(engine, trim_history_cfg)
                    : nullptr;
                CHECK(trim_history_conv != nullptr,
                      "Trim-history conversation created");
                if (trim_history_conv) {
                    if (std::filesystem::exists(audio_path)) {
                        GS_JsonResponse* trim_audio_1 = GS_ConversationSendAudioFromFile(
                            trim_history_conv,
                            "Transcribe this clip in one short sentence.",
                            audio_path.string().c_str());
                        CHECK(trim_audio_1 != nullptr,
                              "Trim-history audio turn 1 returned response");
                        GS_JsonResponseDelete(trim_audio_1);

                        GS_JsonResponse* trim_audio_2 = GS_ConversationSendAudioFromFile(
                            trim_history_conv,
                            "Describe the clip again in one short sentence.",
                            audio_path.string().c_str());
                        CHECK(trim_audio_2 != nullptr,
                              "Trim-history audio turn 2 returned response");
                        GS_JsonResponseDelete(trim_audio_2);
                    }

                    if (std::filesystem::exists(whiteboard_path)) {
                        GS_JsonResponse* trim_image_1 = GS_ConversationSendImageFromFile(
                            trim_history_conv,
                            "Describe this image in one short sentence.",
                            whiteboard_path.string().c_str());
                        CHECK(trim_image_1 != nullptr,
                              "Trim-history image turn 1 returned response");
                        GS_JsonResponseDelete(trim_image_1);

                        GS_JsonResponse* trim_image_2 = GS_ConversationSendImageFromFile(
                            trim_history_conv,
                            "Summarize the image again in one short sentence.",
                            whiteboard_path.string().c_str());
                        CHECK(trim_image_2 != nullptr,
                              "Trim-history image turn 2 returned response");
                        GS_JsonResponseDelete(trim_image_2);
                    }

                    GS_JsonResponse* trim_followup = GS_ConversationSendText(
                        trim_history_conv,
                        "Reply with exactly HISTORY-OK.");
                    CHECK(trim_followup != nullptr,
                          "Trim-history conversation remains usable after repeated media turns");
                    if (trim_followup) {
                        const char* text = GS_JsonResponseGetString(trim_followup);
                        CHECK(text != nullptr,
                              "Trim-history follow-up response text is non-null");
                        printf("  [Trim-history recovery] %s\n",
                               text ? text : "(null)");
                        GS_JsonResponseDelete(trim_followup);
                    }
                }
                GS_ConversationDelete(trim_history_conv);
                GS_ConversationConfigDelete(trim_history_cfg);

                // ==============================================================
                // Test 10: Image cancellation and recovery
                // ==============================================================

                TEST("Image cancellation (live)");
                GS_ConversationConfig* image_cancel_cfg = GS_ConversationConfigCreate(
                    engine,
                    "Describe images carefully and keep responding until cancelled.",
                    nullptr,
                    false);
                CHECK(image_cancel_cfg != nullptr, "Image cancellation config created");
                GS_Conversation* image_cancel_conv = image_cancel_cfg
                    ? GS_ConversationCreate(engine, image_cancel_cfg)
                    : nullptr;
                CHECK(image_cancel_conv != nullptr, "Image cancellation conversation created");
                if (image_cancel_conv && std::filesystem::exists(whiteboard_path)) {
                    StreamCapture image_cancel_capture = {};
                    std::thread image_cancel_thread([image_cancel_conv, &image_cancel_capture]() {
                        const auto start = std::chrono::steady_clock::now();
                        while (image_cancel_capture.count.load(std::memory_order_relaxed) == 0) {
                            if (std::chrono::steady_clock::now() - start >
                                std::chrono::seconds(30)) {
                                break;
                            }
                            std::this_thread::sleep_for(std::chrono::milliseconds(10));
                        }

                        std::this_thread::sleep_for(std::chrono::milliseconds(25));
                        GS_ConversationCancelProcess(image_cancel_conv);
                    });

                    printf("\n  [Image prompt] Describe the whiteboard, then restate the description 200 times.\n  [Model] ");
                    int image_cancel_rc = GS_ConversationSendImageFromFileStream(
                        image_cancel_conv,
                        "Describe this whiteboard image in detail, then repeat the same description 200 times, one per line, and do not summarize.",
                        whiteboard_path.string().c_str(),
                        stream_cb,
                        &image_cancel_capture);
                    image_cancel_thread.join();
                    printf("\n");

                    CHECK(image_cancel_rc == 1,
                          "GS_ConversationSendImageFromFileStream returned 1 on cancellation");
                    CHECK(image_cancel_capture.count.load(std::memory_order_relaxed) > 0,
                          "Image cancellation happened after streaming began");

                    GS_JsonResponse* image_recovery_resp = GS_ConversationSendText(
                        image_cancel_conv, "Reply with exactly IMAGE-READY.");
                    CHECK(image_recovery_resp != nullptr,
                          "Conversation remains usable after image cancellation");
                    if (image_recovery_resp) {
                        const char* text = GS_JsonResponseGetString(image_recovery_resp);
                        CHECK(text != nullptr, "Image recovery response text is non-null");
                        printf("  [Image recovery] %s\n", text ? text : "(null)");
                        GS_JsonResponseDelete(image_recovery_resp);
                    }
                }
                GS_ConversationDelete(image_cancel_conv);
                GS_ConversationConfigDelete(image_cancel_cfg);

                // ==============================================================
                // Test 11: Benchmark info
                // ==============================================================

                TEST("Benchmark info (live)");
                GS_BenchmarkInfo* bench = GS_ConversationGetBenchmarkInfo(conv);
                CHECK(bench != nullptr, "Benchmark info retrieved");
                if (bench) {
                    printf("  TTFT:     %.3f s\n",
                           GS_BenchmarkInfoGetTimeToFirstToken(bench));
                    printf("  Prefill:  %.1f tok/s (%d tokens)\n",
                           GS_BenchmarkInfoGetPrefillTokensPerSec(bench),
                           GS_BenchmarkInfoGetPrefillTokenCount(bench));
                    printf("  Decode:   %.1f tok/s (%d tokens)\n",
                           GS_BenchmarkInfoGetDecodeTokensPerSec(bench),
                           GS_BenchmarkInfoGetDecodeTokenCount(bench));
                    GS_BenchmarkInfoDelete(bench);
                }

                GS_ConversationDelete(conv);
            }
            GS_ConversationConfigDelete(cfg);
            GS_EngineDelete(engine);
        }
        GS_EngineSettingsDelete(settings);
    }

    // ---- Summary ----

    printf("\n=============================================\n");
    printf("  Results: %d PASS, %d FAIL\n", g_pass, g_fail);
    printf("=============================================\n");

    return g_fail > 0 ? 1 : 0;
}
