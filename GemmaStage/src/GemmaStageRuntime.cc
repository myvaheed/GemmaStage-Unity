#include "GemmaStageInternal.h"

#ifdef _WIN32
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#endif

std::atomic<int> g_min_log_level{1};
std::mutex       g_backend_runtime_mutex;
size_t           g_backend_runtime_refcount = 0;

// Returns the directory containing this DLL (e.g. Plugins/x86_64 under Unity),
// or an empty path if it can't be resolved. ggml_backend_load_all() defaults
// to scanning the executable directory and CWD, which for an Editor-hosted
// plugin are Unity.exe and the project root — neither contains the ggml-*.dll
// backend siblings. Pointing ggml at this DLL's own directory is the proper
// fix and replaces the older CWD-swap workaround on the Unity side.
static std::filesystem::path gs_get_self_module_dir() {
#ifdef _WIN32
    HMODULE self = nullptr;
    if (!GetModuleHandleExW(
            GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
                GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
            reinterpret_cast<LPCWSTR>(&gs_get_self_module_dir),
            &self) ||
        !self) {
        return {};
    }
    wchar_t buf[MAX_PATH];
    DWORD len = GetModuleFileNameW(self, buf, MAX_PATH);
    if (len == 0 || len >= MAX_PATH) {
        return {};
    }
    std::error_code ec;
    auto p = std::filesystem::path(buf).parent_path();
    return std::filesystem::exists(p, ec) ? p : std::filesystem::path{};
#else
    return {};
#endif
}

void gs_log(int level, const char* fmt, ...) {
    if (level < g_min_log_level.load(std::memory_order_relaxed)) {
        return;
    }

    const char* tag = "???";
    switch (level) {
        case 0: tag = "DEBUG"; break;
        case 1: tag = "INFO";  break;
        case 2: tag = "WARN";  break;
        case 3: tag = "ERROR"; break;
    }

    fprintf(stderr, "[GemmaStage][%s] ", tag);
    va_list args;
    va_start(args, fmt);
    vfprintf(stderr, fmt, args);
    va_end(args);
    fprintf(stderr, "\n");
}

std::string NormalizePath(const char* path) {
    if (!path || !path[0]) {
        return {};
    }

    std::error_code ec;
    auto abs = std::filesystem::absolute(std::filesystem::path(path), ec);
    std::string normalized = ec ? std::string(path) : abs.string();
    std::replace(normalized.begin(), normalized.end(), '\\', '/');
    return normalized;
}

std::string ToLowerCopy(const std::string& value) {
    std::string out = value;
    std::transform(out.begin(), out.end(), out.begin(), [](unsigned char ch) {
        return static_cast<char>(std::tolower(ch));
    });
    return out;
}

static GS_BackendPreference ParseBackendPreference(const std::string& backend) {
    const std::string value = ToLowerCopy(backend);
    if (value.empty() || value == "auto") {
        return GS_BackendPreference::Auto;
    }
    if (value == "cuda") {
        return GS_BackendPreference::CUDA;
    }
    if (value == "vulkan") {
        return GS_BackendPreference::Vulkan;
    }
    if (value == "cpu") {
        return GS_BackendPreference::CPU;
    }
    return GS_BackendPreference::Auto;
}

static const char* BackendPreferenceName(GS_BackendPreference preference) {
    switch (preference) {
        case GS_BackendPreference::Auto:   return "auto";
        case GS_BackendPreference::CUDA:   return "cuda";
        case GS_BackendPreference::Vulkan: return "vulkan";
        case GS_BackendPreference::CPU:    return "cpu";
    }
    return "auto";
}

static int MapGgmlLogLevel(enum ggml_log_level level) {
    switch (level) {
        case GGML_LOG_LEVEL_DEBUG: return 0;
        case GGML_LOG_LEVEL_INFO:  return 1;
        case GGML_LOG_LEVEL_WARN:  return 2;
        case GGML_LOG_LEVEL_ERROR: return 3;
        case GGML_LOG_LEVEL_NONE:
        case GGML_LOG_LEVEL_CONT:
            return 4;
    }
    return 1;
}

static void gs_llama_log_callback(enum ggml_log_level level, const char* text, void* user_data) {
    (void)user_data;
    if (!text) {
        return;
    }

    const int mapped_level = MapGgmlLogLevel(level);
    if (mapped_level < g_min_log_level.load(std::memory_order_relaxed)) {
        return;
    }

    if (level == GGML_LOG_LEVEL_CONT) {
        fputs(text, stderr);
        return;
    }

    const char* tag = "INFO";
    switch (mapped_level) {
        case 0: tag = "DEBUG"; break;
        case 1: tag = "INFO";  break;
        case 2: tag = "WARN";  break;
        case 3: tag = "ERROR"; break;
    }

    fprintf(stderr, "[GemmaStage][llama.cpp][%s] %s", tag, text);
    const size_t len = strlen(text);
    if (len == 0 || text[len - 1] != '\n') {
        fputc('\n', stderr);
    }
}

static bool BackendRegNameEquals(ggml_backend_reg_t reg, const char* expected);

// Pre-loads dependency DLLs from `dir` using LOAD_WITH_ALTERED_SEARCH_PATH so
// Windows resolves their own imports relative to `dir`. Workaround for a CUDA
// backend load failure observed in Unity Editor on Windows.
//
// Background — when ggml's backend loader does plain LoadLibrary("<abs>\ggml-cuda.dll"),
// Windows resolves ggml-cuda's *implicit* imports (cudart64_12, cublas64_12,
// cublasLt64_12) using the standard search order: app .exe folder → System32
// → Windows → CWD → PATH. For a Unity Editor host the .exe folder is the
// Unity install (`C:\Program Files\Unity\…\Editor\`), which obviously has
// none of these — so the dependency probe fails and LoadLibrary on ggml-cuda
// returns NULL. ggml's loader silences that failure in Release (NDEBUG)
// builds, and dl_error() returns an empty string anyway because Windows
// FormatMessage can't always describe a dependency-resolution failure.
//
// We confirmed via Process.Modules dump that the Unity process has NO
// pre-loaded cudart/cublas — there is no DLL-version collision; it's purely
// a search-path miss. test_load.exe works because the exe itself lives in
// the same folder as the CUDA stubs, so the default "exe folder" lookup
// happens to hit the right directory.
//
// LOAD_WITH_ALTERED_SEARCH_PATH tells the loader to substitute the loaded
// DLL's directory for the .exe folder at the head of the search order for
// THIS call (and its dependency resolution). Pre-loading the three CUDA
// stubs this way pulls them into the process from Plugins/x86_64/. When
// ggml's later LoadLibrary asks for cudart64_12.dll by name, Windows finds
// the already-loaded module and binds without searching. ggml-cuda's
// DllMain then runs cleanly, cuInit() finds nvcuda64.dll in System32, and
// the backend registers.
//
// Note: LOAD_WITH_ALTERED_SEARCH_PATH is technically deprecated in favor of
// SetDefaultDllDirectories(LOAD_LIBRARY_SEARCH_DEFAULT_DIRS) + LoadLibraryEx
// with LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR. Both are still supported on every
// Windows version we target; the deprecated flag is fine here.
static void gs_preload_with_altered_search(const std::filesystem::path& dir,
                                           std::initializer_list<const char*> names) {
#ifdef _WIN32
    std::error_code ec;
    for (const char* name : names) {
        const auto dll_path = dir / std::filesystem::u8path(name);
        if (!std::filesystem::exists(dll_path, ec)) continue;
        const std::wstring wide = dll_path.wstring();
        HMODULE h = LoadLibraryExW(wide.c_str(), nullptr, LOAD_WITH_ALTERED_SEARCH_PATH);
        if (!h) {
            const DWORD err = GetLastError();
            gs_log(2, "Preload failed: %s (Win32 error %lu)",
                   dll_path.string().c_str(), static_cast<unsigned long>(err));
        }
        // Leak the handle on purpose — DLL stays resident for the process
        // lifetime so any later LoadLibrary on the same name is a no-op.
    }
#else
    (void)dir;
    (void)names;
#endif
}

// Returns true if any registered backend reg has the given name (case-insensitive).
static bool gs_has_backend(const char* expected_name) {
    if (!expected_name) return false;
    for (size_t i = 0; i < ggml_backend_reg_count(); ++i) {
        ggml_backend_reg_t reg = ggml_backend_reg_get(i);
        if (reg && BackendRegNameEquals(reg, expected_name)) {
            return true;
        }
    }
    return false;
}

void gs_backend_runtime_acquire() {
    std::lock_guard<std::mutex> lock(g_backend_runtime_mutex);
    if (g_backend_runtime_refcount == 0) {
        llama_log_set(gs_llama_log_callback, nullptr);
        llama_backend_init();

        const auto self_dir = gs_get_self_module_dir();
        if (!self_dir.empty()) {
            const auto self_dir_str = self_dir.string();
            gs_log(1, "Loading ggml backends from %s", self_dir_str.c_str());

            // Pre-load CUDA backend deps so ggml-cuda.dll's silent
            // LoadLibrary (without LOAD_WITH_ALTERED_SEARCH_PATH) finds its
            // imports in this folder rather than the host .exe's folder.
            gs_preload_with_altered_search(self_dir, {
                "cudart64_12.dll",
                "cublas64_12.dll",
                "cublasLt64_12.dll",
            });

            ggml_backend_load_all_from_path(self_dir_str.c_str());

            // ggml_backend_load_all_from_path silences load failures in Release
            // (NDEBUG) builds. If an expected backend DLL is present on disk
            // but didn't end up registered, re-probe it explicitly via
            // ggml_backend_load() — that path runs with silent=false so the
            // real error (missing dep, init failure, …) lands in the log.
            const struct { const char* dll; const char* reg_name; } expected[] = {
                {"ggml-cuda.dll",   "CUDA"},
                {"ggml-vulkan.dll", "Vulkan"},
            };
            std::error_code ec;
            for (const auto& probe : expected) {
                if (gs_has_backend(probe.reg_name)) continue;
                const auto dll_path = self_dir / std::filesystem::u8path(probe.dll);
                if (!std::filesystem::exists(dll_path, ec)) continue;
                const auto dll_path_str = dll_path.string();
                gs_log(2, "Backend '%s' not registered after batch load; verbose probe of %s",
                       probe.reg_name, dll_path_str.c_str());
                ggml_backend_load(dll_path_str.c_str());
            }
        } else {
            gs_log(2, "Could not resolve self module directory; falling back to default ggml backend search.");
            ggml_backend_load_all();
        }
        gs_log(1, "llama.cpp runtime initialised and ggml backends loaded.");
    }
    ++g_backend_runtime_refcount;
}

void gs_backend_runtime_release() {
    std::lock_guard<std::mutex> lock(g_backend_runtime_mutex);
    if (g_backend_runtime_refcount == 0) {
        return;
    }

    --g_backend_runtime_refcount;
    if (g_backend_runtime_refcount == 0) {
        llama_backend_free();
        gs_log(1, "llama.cpp runtime released.");
    }
}

static bool BackendRegNameEquals(ggml_backend_reg_t reg, const char* expected) {
    if (!reg || !expected) {
        return false;
    }
    return ToLowerCopy(ggml_backend_reg_name(reg)) == ToLowerCopy(expected);
}

static void LogRegisteredBackends() {
    const size_t reg_count = ggml_backend_reg_count();
    const size_t dev_count = ggml_backend_dev_count();
    gs_log(1, "ggml registry: %zu backend(s), %zu device(s)", reg_count, dev_count);

    for (size_t i = 0; i < dev_count; ++i) {
        ggml_backend_dev_t dev = ggml_backend_dev_get(i);
        if (!dev) {
            continue;
        }

        ggml_backend_reg_t reg = ggml_backend_dev_backend_reg(dev);
        ggml_backend_dev_props props = {};
        ggml_backend_dev_get_props(dev, &props);

        gs_log(0,
               "  device[%zu]: reg=%s name=%s type=%d free=%zu total=%zu desc=%s",
               i,
               reg ? ggml_backend_reg_name(reg) : "(none)",
               ggml_backend_dev_name(dev),
               static_cast<int>(ggml_backend_dev_type(dev)),
               props.memory_free,
               props.memory_total,
               ggml_backend_dev_description(dev));
    }
}

static bool ProbeBackendDevice(ggml_backend_dev_t device) {
    if (!device) {
        return false;
    }

    ggml_backend_t backend = ggml_backend_dev_init(device, nullptr);
    if (!backend) {
        return false;
    }

    ggml_backend_free(backend);
    return true;
}

static ggml_backend_dev_t FindPreferredDevice(GS_BackendPreference preference) {
    if (preference == GS_BackendPreference::CPU) {
        ggml_backend_dev_t cpu = ggml_backend_dev_by_type(GGML_BACKEND_DEVICE_TYPE_CPU);
        return ProbeBackendDevice(cpu) ? cpu : nullptr;
    }

    const char* expected_reg_name =
        preference == GS_BackendPreference::CUDA ? "CUDA" : "Vulkan";

    for (size_t i = 0; i < ggml_backend_dev_count(); ++i) {
        ggml_backend_dev_t dev = ggml_backend_dev_get(i);
        if (!dev) {
            continue;
        }

        ggml_backend_reg_t reg = ggml_backend_dev_backend_reg(dev);
        if (!BackendRegNameEquals(reg, expected_reg_name)) {
            continue;
        }

        const enum ggml_backend_dev_type dev_type = ggml_backend_dev_type(dev);
        if (dev_type != GGML_BACKEND_DEVICE_TYPE_GPU &&
            dev_type != GGML_BACKEND_DEVICE_TYPE_IGPU) {
            continue;
        }

        if (ProbeBackendDevice(dev)) {
            return dev;
        }
    }

    return nullptr;
}

bool SelectBackend(const GS_EngineSettings& settings, GS_BackendSelection* selection) {
    if (!selection) {
        return false;
    }

    const GS_BackendPreference requested = ParseBackendPreference(settings.backend);
    if (!settings.backend.empty() && requested == GS_BackendPreference::Auto &&
        ToLowerCopy(settings.backend) != "auto") {
        gs_log(2, "Unknown backend override \"%s\"; falling back to automatic selection.",
               settings.backend.c_str());
    }

    auto assign = [selection](GS_BackendPreference preference, ggml_backend_dev_t device) {
        selection->device = device;
        selection->preference = preference;
        selection->backend_name =
            preference == GS_BackendPreference::CUDA   ? "CUDA" :
            preference == GS_BackendPreference::Vulkan ? "Vulkan" :
                                                         "CPU";
        selection->gpu_enabled = preference != GS_BackendPreference::CPU;
    };

    if (requested != GS_BackendPreference::Auto) {
        ggml_backend_dev_t forced_device = FindPreferredDevice(requested);
        if (!forced_device) {
            gs_log(3, "Requested backend override \"%s\" is unavailable on this machine.",
                   BackendPreferenceName(requested));
            return false;
        }

        assign(requested, forced_device);
        return true;
    }

    const GS_BackendPreference order[] = {
        GS_BackendPreference::CUDA,
        GS_BackendPreference::Vulkan,
        GS_BackendPreference::CPU,
    };

    for (GS_BackendPreference preference : order) {
        ggml_backend_dev_t device = FindPreferredDevice(preference);
        if (device) {
            assign(preference, device);
            return true;
        }
    }

    gs_log(3, "No compatible ggml backend device was found.");
    return false;
}

extern "C" {

GS_API void GS_SetMinLogLevel(int level) {
    g_min_log_level.store(level, std::memory_order_relaxed);
    llama_log_set(gs_llama_log_callback, nullptr);
}

GS_API GS_EngineSettings* GS_EngineSettingsCreate(
    const char* model_path,
    const char* mmproj_path,
    const char* backend)
{
    auto* settings = new GS_EngineSettings();
    settings->model_path = model_path ? NormalizePath(model_path) : "";
    settings->mmproj_path = mmproj_path ? NormalizePath(mmproj_path) : "";
    settings->backend = backend ? backend : "";

    gs_log(1, "EngineSettings created: model=%s mmproj=%s backend=%s",
           settings->model_path.c_str(),
           settings->mmproj_path.empty() ? "(none)" : settings->mmproj_path.c_str(),
           settings->backend.empty() ? "auto" : settings->backend.c_str());
    return settings;
}

GS_API void GS_EngineSettingsDelete(GS_EngineSettings* settings) {
    delete settings;
}

GS_API void GS_EngineSettingsSetContextSize(GS_EngineSettings* settings, int n_ctx) {
    if (settings) {
        settings->n_ctx = n_ctx;
    }
}

GS_API void GS_EngineSettingsSetGpuLayers(GS_EngineSettings* settings, int n_gpu_layers) {
    if (settings) {
        settings->n_gpu_layers = n_gpu_layers;
    }
}

GS_API void GS_EngineSettingsEnableBenchmark(GS_EngineSettings* settings) {
    if (settings) {
        settings->benchmark = true;
    }
}

GS_API GS_Engine* GS_EngineCreate(const GS_EngineSettings* settings) {
    if (!settings) {
        gs_log(3, "EngineCreate: settings is NULL");
        return nullptr;
    }
    if (settings->model_path.empty()) {
        gs_log(3, "EngineCreate: model_path is empty");
        return nullptr;
    }

    gs_log(1, "Preparing llama.cpp runtime (backend=%s)...",
           settings->backend.empty() ? "auto" : settings->backend.c_str());
    gs_backend_runtime_acquire();
    LogRegisteredBackends();

    GS_BackendSelection backend_selection = {};
    if (!SelectBackend(*settings, &backend_selection)) {
        gs_backend_runtime_release();
        return nullptr;
    }

    llama_model_params model_params = llama_model_default_params();
    std::vector<ggml_backend_dev_t> selected_devices = {
        backend_selection.device,
        nullptr,
    };
    model_params.devices = selected_devices.data();
    model_params.n_gpu_layers = backend_selection.gpu_enabled ? settings->n_gpu_layers : 0;

    gs_log(1, "Loading model: %s (backend=%s, gpu_layers=%d)",
           settings->model_path.c_str(),
           backend_selection.backend_name.c_str(),
           model_params.n_gpu_layers);

    llama_model* model = llama_model_load_from_file(
        settings->model_path.c_str(), model_params);
    if (!model) {
        gs_log(3, "Failed to load model from %s", settings->model_path.c_str());
        gs_backend_runtime_release();
        return nullptr;
    }

    gs_log(1, "Model loaded successfully.");

    mtmd_context* mtmd_ctx = nullptr;
    if (!settings->mmproj_path.empty()) {
        gs_log(1, "Loading mmproj: %s", settings->mmproj_path.c_str());
        mtmd_context_params mtmd_params = mtmd_context_params_default();
        mtmd_params.use_gpu = backend_selection.gpu_enabled;
        // OCR/document parsing favors the highest Gemma 4 visual token budget
        // so small text survives the projector path with minimal detail loss.
        mtmd_params.image_min_tokens = 1120;
        mtmd_params.image_max_tokens = 1120;
        mtmd_ctx = mtmd_init_from_file(
            settings->mmproj_path.c_str(), model, mtmd_params);
        if (!mtmd_ctx) {
            gs_log(2, "Failed to load mmproj - multimodal disabled.");
        } else {
            gs_log(1, "mmproj loaded. Vision=%s Audio=%s",
                   mtmd_support_vision(mtmd_ctx) ? "yes" : "no",
                   mtmd_support_audio(mtmd_ctx) ? "yes" : "no");
        }
    }

    auto* engine = new GS_Engine();
    engine->model = model;
    engine->mtmd = mtmd_ctx;
    engine->backend_name = backend_selection.backend_name;
    engine->benchmark = settings->benchmark;
    engine->gpu_enabled = backend_selection.gpu_enabled;
    engine->n_ctx = settings->n_ctx > 0 ? static_cast<uint32_t>(settings->n_ctx) : 0;

    gs_log(1, "Engine created. Backend: %s", engine->backend_name.c_str());
    return engine;
}

GS_API void GS_EngineDelete(GS_Engine* engine) {
    if (!engine) {
        return;
    }

    gs_log(1, "Destroying engine...");
    if (engine->mtmd) {
        mtmd_free(engine->mtmd);
    }
    if (engine->model) {
        llama_model_free(engine->model);
    }

    delete engine;
    gs_backend_runtime_release();
    gs_log(1, "Engine destroyed.");
}

GS_API const char* GS_EngineGetBackendName(const GS_Engine* engine) {
    return engine ? engine->backend_name.c_str() : "unknown";
}

GS_API GS_ConversationConfig* GS_ConversationConfigCreate(
    GS_Engine* engine,
    const char* system_message,
    const char* tools_json,
    bool enable_constrained_decoding)
{
    (void)engine;
    auto* config = new GS_ConversationConfig();
    config->system_message = system_message ? system_message : "";
    config->tools_json = tools_json ? tools_json : "";
    config->constrained_decoding = enable_constrained_decoding;

    gs_log(1, "ConversationConfig created. system_msg=%zuB tools=%zuB constrained=%s",
           config->system_message.size(),
           config->tools_json.size(),
           config->constrained_decoding ? "yes" : "no");
    return config;
}

GS_API void GS_ConversationConfigDelete(GS_ConversationConfig* config) {
    delete config;
}

GS_API void GS_ConversationConfigSetRetainTextOnlyMediaHistory(
    GS_ConversationConfig* config,
    bool enable)
{
    if (!config) {
        return;
    }
    config->retain_text_only_media_history = enable;
}

GS_API void GS_ConversationConfigSetEnableThinking(
    GS_ConversationConfig* config,
    bool enable)
{
    if (!config) {
        return;
    }
    config->enable_thinking = enable;
}

GS_API void GS_ConversationConfigSetContextSizeOverride(
    GS_ConversationConfig* config,
    int n_ctx)
{
    if (!config) {
        return;
    }
    config->n_ctx_override = n_ctx > 0 ? n_ctx : 0;
}

}  // extern "C"
