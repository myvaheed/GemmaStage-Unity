/*
 * test_poc.cc -- Iterative audio PoC console for GS-54
 *
 * Usage:
 *   test_poc.exe <model.gguf> <mmproj.gguf> [backend] [n_ctx]
 *                [--media-history=full|text-only] [--csv_output=<path>] [--verbose]
 *
 * The executable expects audio_sample.wav to live beside the .exe. It keeps a
 * single conversation alive, replays the same audio clip on every iteration,
 * streams the model response to stdout, and prints one compact stats line per
 * turn until Ctrl+C or the context window is nearly full.
 */

#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <Windows.h>
#include <Psapi.h>
#include <dxgi1_6.h>

#include "GemmaStage.h"

#include <fcntl.h>
#include <io.h>
#include <algorithm>
#include <atomic>
#include <chrono>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <string>
#include <vector>

namespace {

struct PocArgs {
    const char* model_path = nullptr;
    const char* mmproj_path = nullptr;
    const char* backend_override = nullptr;
    const char* csv_output_path = nullptr;
    int n_ctx = 32768;
    bool retain_text_only_media_history = false;
    bool verbose_logs = false;
};

struct StreamCapture {
    std::chrono::steady_clock::time_point started_at;
    std::chrono::steady_clock::time_point first_chunk_at;
    bool saw_first_chunk = false;
    bool printed_prefix = false;
    bool echo = false;
    std::string text;
};

struct MemorySnapshot {
    std::string source;
    uint64_t bytes = 0;
    bool ok = false;
};

std::atomic<bool> g_stop_requested{false};
std::atomic<GS_Conversation*> g_active_conversation{nullptr};

bool streq_nocase(const char* a, const char* b) {
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

bool is_backend_name(const char* value) {
    return streq_nocase(value, "auto") ||
           streq_nocase(value, "cuda") ||
           streq_nocase(value, "vulkan") ||
           streq_nocase(value, "cpu");
}

bool is_numeric(const char* value) {
    if (!value || !*value) {
        return false;
    }
    while (*value) {
        if (*value < '0' || *value > '9') {
            return false;
        }
        ++value;
    }
    return true;
}

bool try_parse_media_history_arg(
    const char* value,
    bool* retain_text_only_media_history)
{
    constexpr char kPrefix[] = "--media-history=";
    if (!value || !retain_text_only_media_history ||
        std::strncmp(value, kPrefix, sizeof(kPrefix) - 1) != 0) {
        return false;
    }

    const char* mode = value + (sizeof(kPrefix) - 1);
    if (streq_nocase(mode, "text-only") || streq_nocase(mode, "on")) {
        *retain_text_only_media_history = true;
        return true;
    }
    if (streq_nocase(mode, "full") || streq_nocase(mode, "off")) {
        *retain_text_only_media_history = false;
        return true;
    }
    return false;
}

bool try_parse_csv_output_arg(
    int         argc,
    char*       argv[],
    int*        index,
    const char** csv_output_path)
{
    if (!index || !csv_output_path) {
        return false;
    }

    const char* value = argv[*index];
    if (!value) {
        return false;
    }

    constexpr char kCsvPrefix[] = "--csv_output=";
    if (std::strncmp(value, kCsvPrefix, sizeof(kCsvPrefix) - 1) == 0) {
        const char* path = value + (sizeof(kCsvPrefix) - 1);
        if (path[0] == '\0') {
            return false;
        }
        *csv_output_path = path;
        return true;
    }

    if (std::strcmp(value, "--csv_output") != 0) {
        return false;
    }

    if (*index + 1 >= argc || !argv[*index + 1] || argv[*index + 1][0] == '\0') {
        return false;
    }

    ++(*index);
    *csv_output_path = argv[*index];
    return true;
}

double safe_tokens_per_second(int tokens, double seconds) {
    return (tokens > 0 && seconds > 0.0)
        ? (static_cast<double>(tokens) / seconds)
        : 0.0;
}

struct ScopedStdSilence {
    int stdout_dup = -1;
    int stderr_dup = -1;
    int null_fd = -1;
    bool active = false;

    explicit ScopedStdSilence(bool enable) {
        if (!enable) {
            return;
        }

        std::fflush(stdout);
        std::fflush(stderr);

        null_fd = _open("NUL", _O_WRONLY);
        if (null_fd < 0) {
            return;
        }

        stdout_dup = _dup(_fileno(stdout));
        stderr_dup = _dup(_fileno(stderr));
        if (stdout_dup < 0 || stderr_dup < 0) {
            return;
        }

        if (_dup2(null_fd, _fileno(stdout)) != 0 ||
            _dup2(null_fd, _fileno(stderr)) != 0) {
            return;
        }

        active = true;
    }

    ~ScopedStdSilence() {
        if (active) {
            std::fflush(stdout);
            std::fflush(stderr);
            _dup2(stdout_dup, _fileno(stdout));
            _dup2(stderr_dup, _fileno(stderr));
        }
        if (stdout_dup >= 0) {
            _close(stdout_dup);
        }
        if (stderr_dup >= 0) {
            _close(stderr_dup);
        }
        if (null_fd >= 0) {
            _close(null_fd);
        }
    }
};

PocArgs parse_args(int argc, char* argv[]) {
    PocArgs args;
    if (argc > 1) {
        args.model_path = argv[1];
    }
    if (argc > 2) {
        args.mmproj_path = argv[2];
    }
    for (int i = 3; i < argc; ++i) {
        if (std::strcmp(argv[i], "--verbose") == 0) {
            args.verbose_logs = true;
        } else if (try_parse_csv_output_arg(
                       argc,
                       argv,
                       &i,
                       &args.csv_output_path)) {
            continue;
        } else if (try_parse_media_history_arg(
                       argv[i],
                       &args.retain_text_only_media_history)) {
            continue;
        } else if (is_backend_name(argv[i])) {
            args.backend_override = argv[i];
        } else if (is_numeric(argv[i])) {
            args.n_ctx = std::atoi(argv[i]);
        }
    }
    return args;
}

std::filesystem::path executable_dir() {
    wchar_t buffer[MAX_PATH] = {};
    const DWORD count = GetModuleFileNameW(nullptr, buffer, MAX_PATH);
    if (count == 0 || count == MAX_PATH) {
        return std::filesystem::current_path();
    }
    return std::filesystem::path(buffer).parent_path();
}

std::vector<unsigned char> read_binary_file(const std::filesystem::path& path) {
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

std::string format_bytes(uint64_t bytes) {
    static const char* const units[] = { "B", "KB", "MB", "GB", "TB" };
    double value = static_cast<double>(bytes);
    size_t unit_index = 0;
    while (value >= 1024.0 && unit_index + 1 < (sizeof(units) / sizeof(units[0]))) {
        value /= 1024.0;
        ++unit_index;
    }

    char buffer[64];
    std::snprintf(buffer, sizeof(buffer), "%.2f %s", value, units[unit_index]);
    return buffer;
}

MemorySnapshot query_working_set() {
    PROCESS_MEMORY_COUNTERS_EX counters = {};
    counters.cb = sizeof(counters);
    if (!GetProcessMemoryInfo(
            GetCurrentProcess(),
            reinterpret_cast<PROCESS_MEMORY_COUNTERS*>(&counters),
            sizeof(counters))) {
        return {};
    }

    MemorySnapshot snapshot;
    snapshot.source = "WorkingSet";
    snapshot.bytes = static_cast<uint64_t>(counters.WorkingSetSize);
    snapshot.ok = true;
    return snapshot;
}

MemorySnapshot query_dxgi_local_usage() {
    IDXGIFactory6* factory = nullptr;
    if (FAILED(CreateDXGIFactory1(IID_PPV_ARGS(&factory))) || !factory) {
        return {};
    }

    IDXGIAdapter1* adapter = nullptr;
    HRESULT hr = factory->EnumAdapterByGpuPreference(
        0,
        DXGI_GPU_PREFERENCE_HIGH_PERFORMANCE,
        IID_PPV_ARGS(&adapter));
    if (FAILED(hr) || !adapter) {
        hr = factory->EnumAdapters1(0, &adapter);
    }

    IDXGIAdapter3* adapter3 = nullptr;
    if (SUCCEEDED(hr) && adapter) {
        hr = adapter->QueryInterface(IID_PPV_ARGS(&adapter3));
    }

    DXGI_QUERY_VIDEO_MEMORY_INFO info = {};
    if (SUCCEEDED(hr) && adapter3) {
        hr = adapter3->QueryVideoMemoryInfo(0, DXGI_MEMORY_SEGMENT_GROUP_LOCAL, &info);
    }

    if (adapter3) {
        adapter3->Release();
    }
    if (adapter) {
        adapter->Release();
    }
    factory->Release();

    if (FAILED(hr)) {
        return {};
    }

    MemorySnapshot snapshot;
    snapshot.source = "DXGI(process)";
    snapshot.bytes = static_cast<uint64_t>(info.CurrentUsage);
    snapshot.ok = true;
    return snapshot;
}

MemorySnapshot query_nvml_process_used() {
    HMODULE nvml = LoadLibraryA("nvml.dll");
    if (!nvml) {
        nvml = LoadLibraryA("nvml64.dll");
    }
    if (!nvml) {
        return {};
    }

    using nvmlReturn_t = int;
    using nvmlDevice_t = void*;
    constexpr unsigned long long NVML_VALUE_NOT_AVAILABLE =
        0xFFFFFFFFFFFFFFFFull;
    struct nvmlMemory_t {
        unsigned long long total;
        unsigned long long free;
        unsigned long long used;
    };
    struct nvmlProcessInfo_t {
        unsigned int pid;
        unsigned long long usedGpuMemory;
    };

    using nvmlInit_t = nvmlReturn_t (*)();
    using nvmlShutdown_t = nvmlReturn_t (*)();
    using nvmlDeviceGetCount_t = nvmlReturn_t (*)(unsigned int*);
    using nvmlDeviceGetHandleByIndex_t = nvmlReturn_t (*)(unsigned int, nvmlDevice_t*);
    using nvmlDeviceGetComputeRunningProcesses_t =
        nvmlReturn_t (*)(nvmlDevice_t, unsigned int*, nvmlProcessInfo_t*);

    const nvmlInit_t init =
        reinterpret_cast<nvmlInit_t>(GetProcAddress(nvml, "nvmlInit_v2"));
    const nvmlShutdown_t shutdown =
        reinterpret_cast<nvmlShutdown_t>(GetProcAddress(nvml, "nvmlShutdown"));
    const nvmlDeviceGetCount_t get_count =
        reinterpret_cast<nvmlDeviceGetCount_t>(
            GetProcAddress(nvml, "nvmlDeviceGetCount_v2"));
    const nvmlDeviceGetHandleByIndex_t get_handle =
        reinterpret_cast<nvmlDeviceGetHandleByIndex_t>(
            GetProcAddress(nvml, "nvmlDeviceGetHandleByIndex_v2"));
    const nvmlDeviceGetComputeRunningProcesses_t get_compute_processes =
        reinterpret_cast<nvmlDeviceGetComputeRunningProcesses_t>(
            GetProcAddress(nvml, "nvmlDeviceGetComputeRunningProcesses"));

    MemorySnapshot snapshot;
    if (!init || !shutdown || !get_count || !get_handle || !get_compute_processes) {
        FreeLibrary(nvml);
        return snapshot;
    }

    const nvmlReturn_t NVML_SUCCESS = 0;
    if (init() != NVML_SUCCESS) {
        FreeLibrary(nvml);
        return snapshot;
    }

    unsigned int device_count = 0;
    if (get_count(&device_count) != NVML_SUCCESS || device_count == 0) {
        shutdown();
        FreeLibrary(nvml);
        return snapshot;
    }

    const unsigned int current_pid = GetCurrentProcessId();
    uint64_t process_bytes = 0;

    for (unsigned int device_index = 0; device_index < device_count; ++device_index) {
        nvmlDevice_t device = nullptr;
        if (get_handle(device_index, &device) != NVML_SUCCESS || !device) {
            continue;
        }

        unsigned int process_count = 64;
        std::vector<nvmlProcessInfo_t> processes(process_count);
        nvmlReturn_t rc = get_compute_processes(
            device, &process_count, processes.data());

        if (process_count > processes.size()) {
            processes.resize(process_count);
            rc = get_compute_processes(device, &process_count, processes.data());
        }

        if (rc != NVML_SUCCESS) {
            continue;
        }

        for (unsigned int i = 0; i < process_count; ++i) {
            if (processes[i].pid == current_pid &&
                processes[i].usedGpuMemory != NVML_VALUE_NOT_AVAILABLE) {
                process_bytes += static_cast<uint64_t>(processes[i].usedGpuMemory);
            }
        }
    }

    if (process_bytes > 0) {
        snapshot.source = "NVML(process)";
        snapshot.bytes = process_bytes;
        snapshot.ok = true;
    }

    shutdown();
    FreeLibrary(nvml);
    return snapshot;
}

MemorySnapshot query_backend_memory(const char* backend_name) {
    if (backend_name && streq_nocase(backend_name, "cuda")) {
        MemorySnapshot nvml = query_nvml_process_used();
        if (nvml.ok) {
            return nvml;
        }
        MemorySnapshot dxgi = query_dxgi_local_usage();
        if (dxgi.ok) {
            return dxgi;
        }
    }

    if (backend_name && streq_nocase(backend_name, "vulkan")) {
        MemorySnapshot dxgi = query_dxgi_local_usage();
        if (dxgi.ok) {
            return dxgi;
        }
    }

    MemorySnapshot fallback = query_working_set();
    if (fallback.ok) {
        fallback.source += "(fallback)";
    }
    return fallback;
}

void print_usage() {
    std::printf("Usage: test_poc.exe <model.gguf> <mmproj.gguf> [backend] [n_ctx] [--media-history=full|text-only] [--csv_output=<path>] [--verbose]\n");
    std::printf("Example: test_poc.exe ..\\..\\models\\gemma.gguf ..\\..\\models\\mmproj.gguf cuda 32768 --media-history=text-only --csv_output=stats.csv\n");
}

BOOL WINAPI console_ctrl_handler(DWORD ctrl_type) {
    switch (ctrl_type) {
        case CTRL_C_EVENT:
        case CTRL_BREAK_EVENT:
        case CTRL_CLOSE_EVENT: {
            g_stop_requested.store(true, std::memory_order_release);
            GS_Conversation* active = g_active_conversation.load(std::memory_order_acquire);
            if (active) {
                GS_ConversationCancelProcess(active);
            }
            return TRUE;
        }
        default:
            return FALSE;
    }
}

void stream_cb(const char* chunk, void* data) {
    auto* capture = static_cast<StreamCapture*>(data);
    if (!chunk || !*chunk || !capture) {
        return;
    }

    if (!capture->saw_first_chunk) {
        capture->first_chunk_at = std::chrono::steady_clock::now();
        capture->saw_first_chunk = true;
    }

    capture->text += chunk;
    if (capture->echo) {
        if (!capture->printed_prefix) {
            std::printf("[RESPONSE] ");
            capture->printed_prefix = true;
        }
        std::printf("%s", chunk);
        std::fflush(stdout);
    }
}

}  // namespace

int main(int argc, char* argv[]) {
    const PocArgs args = parse_args(argc, argv);
    if (!args.model_path || !args.mmproj_path) {
        print_usage();
        return 1;
    }

    if (!SetConsoleCtrlHandler(console_ctrl_handler, TRUE)) {
        std::fprintf(stderr, "Failed to install Ctrl+C handler.\n");
        return 1;
    }

    const std::filesystem::path audio_path = executable_dir() / "audio_sample.wav";
    const std::vector<unsigned char> audio_bytes = read_binary_file(audio_path);
    if (audio_bytes.empty()) {
        std::fprintf(stderr, "audio_sample.wav was not found beside the executable: %s\n",
                     audio_path.string().c_str());
        return 1;
    }

    GS_SetMinLogLevel(args.verbose_logs ? 1 : 2);

    GS_EngineSettings* settings = GS_EngineSettingsCreate(
        args.model_path, args.mmproj_path, args.backend_override);
    if (!settings) {
        std::fprintf(stderr, "Failed to create engine settings.\n");
        return 1;
    }

    GS_EngineSettingsSetContextSize(settings, args.n_ctx);
    GS_EngineSettingsSetGpuLayers(settings, -1);
    GS_EngineSettingsEnableBenchmark(settings);

    GS_Engine* engine = GS_EngineCreate(settings);
    if (!engine) {
        std::fprintf(stderr, "Failed to create engine.\n");
        GS_EngineSettingsDelete(settings);
        return 1;
    }

    constexpr char kSystemPrompt[] =
        "You are an audio monitoring assistant. For each audio clip, keep the answer compact "
        "and use exactly these sections: Transcript, Emotion, Observations. "
        "Do not emit thinking, reasoning traces, or channel tags. "
        "Reply directly with only those three sections. "
        "Transcribe what you hear, estimate the speaker emotion with confidence, "
        "and note a few useful observations or issues.";

    GS_ConversationConfig* config = GS_ConversationConfigCreate(
        engine, kSystemPrompt, nullptr, false);
    GS_ConversationConfigSetRetainTextOnlyMediaHistory(
        config,
        args.retain_text_only_media_history);
    GS_Conversation* conversation = config
        ? GS_ConversationCreate(engine, config)
        : nullptr;

    if (!config || !conversation) {
        std::fprintf(stderr, "Failed to create conversation.\n");
        GS_ConversationDelete(conversation);
        GS_ConversationConfigDelete(config);
        GS_EngineDelete(engine);
        GS_EngineSettingsDelete(settings);
        return 1;
    }

    const char* backend_name = GS_EngineGetBackendName(engine);
    std::ofstream csv_output;
    if (args.csv_output_path) {
        csv_output.open(args.csv_output_path, std::ios::out | std::ios::trunc);
        if (!csv_output) {
            std::fprintf(stderr, "Failed to open CSV output file: %s\n", args.csv_output_path);
            GS_ConversationDelete(conversation);
            GS_ConversationConfigDelete(config);
            GS_EngineDelete(engine);
            GS_EngineSettingsDelete(settings);
            return 1;
        }
        csv_output
            << "iteration,backend,media_history,kv_tokens,context_limit,context_pct,"
            << "vram_bytes,vram_source,rss_bytes,wall_total_s,wall_ttft_s,"
            << "wall_prefill_tok_s,prefill_tokens,wall_decode_tok_s,wall_decode_s,"
            << "decode_tokens,llama_ttft_s,llama_prefill_tok_s,llama_decode_tok_s,"
            << "commit_overhead_s,commit_tokens,"
            << "phase_template_build_s,phase_prefill_s\n";
        csv_output.flush();
    }
    std::printf("=============================================\n");
    std::printf("  GemmaStage test_poc.exe -- GS-54\n");
    std::printf("=============================================\n");
    std::printf("Audio fixture : %s\n", audio_path.string().c_str());
    std::printf("Audio bytes   : %zu\n", audio_bytes.size());
    std::printf("Backend       : %s\n", backend_name ? backend_name : "unknown");
    std::printf("Context size  : %d\n", args.n_ctx);
    std::printf("Media history : %s\n",
                args.retain_text_only_media_history ? "text-only" : "full");
    if (args.csv_output_path) {
        std::printf("CSV output    : %s\n", args.csv_output_path);
    }
    std::printf("Press Ctrl+C to cancel the current iteration and exit.\n\n");

    constexpr char kPrompt[] =
        "Process this audio clip. Keep the three sections short and stable so repeated "
        "iterations are easy to compare. Do not include any hidden reasoning or channel tags.";

    int iteration = 0;
    int exit_code = 0;

    while (!g_stop_requested.load(std::memory_order_acquire)) {
        ++iteration;
        if (args.verbose_logs) {
            std::printf("[ITER %d] starting\n", iteration);
        }

        StreamCapture capture = {};
        capture.started_at = std::chrono::steady_clock::now();
        capture.echo = args.verbose_logs;

        int rc = -1;
        g_active_conversation.store(conversation, std::memory_order_release);
        {
            ScopedStdSilence silence(!args.verbose_logs);
            rc = GS_ConversationSendAudioStream(
                conversation,
                kPrompt,
                audio_bytes.data(),
                audio_bytes.size(),
                stream_cb,
                &capture);
        }
        g_active_conversation.store(nullptr, std::memory_order_release);
        const auto finished_at = std::chrono::steady_clock::now();

        if (capture.printed_prefix) {
            std::printf("\n");
        }

        if (rc == 1 && g_stop_requested.load(std::memory_order_acquire)) {
            std::printf("[INFO] Cancellation requested. Exiting after iteration %d.\n", iteration);
            break;
        }
        if (rc != 0) {
            std::fprintf(stderr, "[ERROR] Audio iteration %d failed (rc=%d).\n", iteration, rc);
            exit_code = 1;
            break;
        }

        GS_BenchmarkInfo* bench = GS_ConversationGetBenchmarkInfo(conversation);
        const double ttft = bench
            ? GS_BenchmarkInfoGetTimeToFirstToken(bench)
            : 0.0;
        const double prefill_tok_s = bench
            ? GS_BenchmarkInfoGetPrefillTokensPerSec(bench)
            : 0.0;
        const double decode_tok_s = bench
            ? GS_BenchmarkInfoGetDecodeTokensPerSec(bench)
            : 0.0;
        const int prefill_tokens = bench
            ? GS_BenchmarkInfoGetPrefillTokenCount(bench)
            : 0;
        const int decode_tokens = bench
            ? GS_BenchmarkInfoGetDecodeTokenCount(bench)
            : 0;
        const double commit_overhead_s = bench
            ? GS_BenchmarkInfoGetCommitOverheadSeconds(bench)
            : 0.0;
        const int commit_tokens = bench
            ? GS_BenchmarkInfoGetCommitTokenCount(bench)
            : 0;
        const double phase_template_build_s = bench
            ? GS_BenchmarkInfoGetTemplateBuildSeconds(bench)
            : 0.0;
        const double phase_prefill_s = bench
            ? GS_BenchmarkInfoGetPrefillSeconds(bench)
            : 0.0;

        const double callback_ttft =
            capture.saw_first_chunk
                ? std::chrono::duration<double>(capture.first_chunk_at - capture.started_at).count()
                : 0.0;
        const double total_turn_s =
            std::chrono::duration<double>(finished_at - capture.started_at).count();
        const double post_first_token_s =
            std::max(0.0, total_turn_s - callback_ttft);
        // Commit runs after the last decoded token but before the turn call
        // returns, so its wall time is inside post_first_token_s. Subtract it
        // to isolate the decode phase from the commit re-prefill.
        const double wall_decode_s =
            std::max(0.0, post_first_token_s - commit_overhead_s);
        const MemorySnapshot vram = query_backend_memory(backend_name);
        const MemorySnapshot rss = query_working_set();
        const int kv_steps = GS_ConversationGetKvCacheTokenCount(conversation);
        const int ctx_limit = GS_ConversationGetContextSize(conversation) > 0
            ? GS_ConversationGetContextSize(conversation)
            : args.n_ctx;
        const double ctx_pct = ctx_limit > 0
            ? (100.0 * static_cast<double>(kv_steps) / static_cast<double>(ctx_limit))
            : 0.0;
        const double wall_prefill_tok_s = safe_tokens_per_second(prefill_tokens, callback_ttft);
        const double wall_decode_tok_s = safe_tokens_per_second(decode_tokens, wall_decode_s);

        char line1[256];
        char line2[256];
        char line3[256];
        char line4[256];
        std::snprintf(
            line1,
            sizeof(line1),
            "[STATS] iter=%d backend=%s kv=%d/%d (%.1f%%) vram=%s via %s rss=%s",
            iteration,
            backend_name ? backend_name : "unknown",
            kv_steps,
            ctx_limit,
            ctx_pct,
            vram.ok ? format_bytes(vram.bytes).c_str() : "n/a",
            vram.ok ? vram.source.c_str() : "unavailable",
            rss.ok ? format_bytes(rss.bytes).c_str() : "n/a");
        std::snprintf(
            line2,
            sizeof(line2),
            "        wall : total=%.3fs ttft=%.3fs prefill=%.1f tok/s (%d) decode=%.1f tok/s (%d)",
            total_turn_s,
            callback_ttft,
            wall_prefill_tok_s,
            prefill_tokens,
            wall_decode_tok_s,
            decode_tokens);
        if (ttft > 0.0 || prefill_tok_s > 0.0 || decode_tok_s > 0.0) {
            std::snprintf(
                line3,
                sizeof(line3),
                "        llama: ttft=%.3fs prefill=%.1f tok/s decode=%.1f tok/s",
                ttft,
                prefill_tok_s,
                decode_tok_s);
        } else {
            std::snprintf(
                line3,
                sizeof(line3),
                "        llama: unavailable for this multimodal path, use wall timings above");
        }
        std::snprintf(
            line4,
            sizeof(line4),
            "        commit: overhead=%.3fs tokens=%d",
            commit_overhead_s,
            commit_tokens);
        char line5[256];
        std::snprintf(
            line5,
            sizeof(line5),
            "        phase: template_build=%.3fs prefill=%.3fs",
            phase_template_build_s,
            phase_prefill_s);

        std::printf("%s\n", line1);
        std::printf("%s\n", line2);
        std::printf("%s\n", line3);
        std::printf("%s\n", line4);
        std::printf("%s\n", line5);

        if (csv_output) {
            csv_output
                << iteration << ','
                << '"' << (backend_name ? backend_name : "unknown") << '"' << ','
                << '"' << (args.retain_text_only_media_history ? "text-only" : "full") << '"' << ','
                << kv_steps << ','
                << ctx_limit << ','
                << ctx_pct << ','
                << (vram.ok ? vram.bytes : 0) << ','
                << '"' << (vram.ok ? vram.source : "unavailable") << '"' << ','
                << (rss.ok ? rss.bytes : 0) << ','
                << total_turn_s << ','
                << callback_ttft << ','
                << wall_prefill_tok_s << ','
                << prefill_tokens << ','
                << wall_decode_tok_s << ','
                << wall_decode_s << ','
                << decode_tokens << ','
                << ttft << ','
                << prefill_tok_s << ','
                << decode_tok_s << ','
                << commit_overhead_s << ','
                << commit_tokens << ','
                << phase_template_build_s << ','
                << phase_prefill_s << '\n';
            csv_output.flush();
        }

        GS_BenchmarkInfoDelete(bench);

        if (ctx_limit > 0 && kv_steps >= (ctx_limit * 95) / 100) {
            std::printf("[INFO] Context window is nearly full. Stopping at iteration %d.\n",
                        iteration);
            break;
        }

        std::printf("\n");
    }

    g_active_conversation.store(nullptr, std::memory_order_release);
    GS_ConversationDelete(conversation);
    GS_ConversationConfigDelete(config);
    GS_EngineDelete(engine);
    GS_EngineSettingsDelete(settings);
    return exit_code;
}
