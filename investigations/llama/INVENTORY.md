# llama.cpp Integration — Inventory & Architecture

> **Task:** GS-39 — Inventory `llama_prebuilt/` and pick the distribution strategy
> **Date:** 2026-04-16
> **Status:** Complete

---

## 1. Upstream llama.cpp Submodule

| Property | Value |
|----------|-------|
| **Location** | `llama.cpp/` (git submodule) |
| **Commit** | `4fbdabdc61c04d1262b581e1b8c0c3b119f688ff` |
| **Commit Date** | 2026-04-16 21:10:22 +0200 |
| **Tagged Release** | **No tag** — HEAD of `master` (untagged) |
| **License** | MIT |

**Key observation:** The repo is pinned to a recent `master` commit with no release tag. The prebuilt archives are labeled `b8816`, which is a *build number* from the GitHub Actions release workflow — not a git tag in the cloned repo. This means:
- The submodule HEAD and the prebuilt **may differ** (the submodule is newer than `b8816`).
- Headers from the submodule should be compatible with `b8816` prebuilts as long as we only use stable C API functions from `llama.h` and `mtmd.h` (both are ABI-stable across minor bumps).
- **Risk:** If we use bleeding-edge API additions, they may be absent from the `b8816` DLLs. Mitigation: compile against the `b8816` headers snapshot, or rebuild llama.cpp from source.

---

## 2. Prebuilt Archive Contents (`llama_prebuilt/`)

Three directories, all for **Windows x64**:

### 2.1 CUDA Build: `llama-b8816-bin-win-cuda-12.4-x64/`

| File | Size | Role |
|------|------|------|
| **llama.dll** | 2.54 MB | Main llama.cpp library (model loading, tokenization, decode, sampling) |
| **mtmd.dll** | 1.07 MB | Multimodal support (vision + audio via mmproj companion file) |
| **ggml.dll** | 0.09 MB | GGML core dispatcher (loads backend plugins) |
| **ggml-base.dll** | 0.74 MB | GGML base operations |
| **ggml-cuda.dll** | **502.25 MB** | CUDA compute backend (cuBLAS GEMM, custom CUDA kernels) |
| **ggml-rpc.dll** | 0.14 MB | Remote Procedure Call backend (distributed inference) |
| **ggml-cpu-*.dll** (×14) | 0.81–1.56 MB each | CPU backends auto-selected by microarchitecture (SSE4.2 → Sapphire Rapids) |
| **libomp140.x86_64.dll** | 0.61 MB | OpenMP runtime (required by CPU backends) |
| Various `.exe` files (×20) | — | CLI tools: `llama-cli`, `llama-server`, `llama-mtmd-cli`, `llama-bench`, etc. |

**Notable absence:** No `.lib` import libraries. The prebuilt is a *runtime-only* distribution — no development headers or linker stubs.

### 2.2 Vulkan Build: `llama-b8816-bin-win-vulkan-x64/`

| File | Size | Role |
|------|------|------|
| **llama.dll** | 2.54 MB | Same as CUDA build (identical) |
| **mtmd.dll** | 1.07 MB | Same as CUDA build (identical) |
| **ggml.dll** | 0.09 MB | Same |
| **ggml-base.dll** | 0.74 MB | Same |
| **ggml-vulkan.dll** | **59.10 MB** | Vulkan compute backend (SPIR-V shaders) |
| **ggml-rpc.dll** | 0.14 MB | Same |
| **ggml-cpu-*.dll** (×14) | 0.81–1.56 MB each | Same CPU variants |
| **libomp140.x86_64.dll** | 0.61 MB | Same |
| Various `.exe` files (×20) | — | Same CLI tools |

**Key difference from CUDA build:** `ggml-cuda.dll` is absent; `ggml-vulkan.dll` is present instead.

### 2.3 CUDA Runtime: `cudart-llama-bin-win-cuda-12.4-x64/`

| File | Size | Role |
|------|------|------|
| **cublas64_12.dll** | 95.40 MB | cuBLAS (matrix math for CUDA) |
| **cublasLt64_12.dll** | 451.61 MB | cuBLAS Light (optimized GEMM routines) |
| **cudart64_12.dll** | 0.53 MB | CUDA runtime |

These are **NVIDIA redistributables** required alongside `ggml-cuda.dll`. Without them, the CUDA backend will fail to load and ggml will fall back to Vulkan or CPU.

---

## 3. Header Files Available (from submodule)

### 3.1 Core llama.cpp Headers (`llama.cpp/include/`)

| Header | Size | Purpose |
|--------|------|---------|
| `llama.h` | 83.8 KB | **Primary C API** — model loading, context creation, decode, sampling, memory/KV cache management, chat template, grammar-based constrained decoding |
| `llama-cpp.h` | 0.9 KB | C++ convenience wrappers (smart pointers) |

### 3.2 GGML Headers (`llama.cpp/ggml/include/`)

| Header | Purpose |
|--------|---------|
| `ggml.h` | Core tensor operations, types, graph building |
| `ggml-backend.h` | Backend registry, device enumeration, buffer management |
| `ggml-alloc.h` | Memory allocator |
| `ggml-cpu.h` | CPU backend specifics |
| `ggml-cuda.h` | CUDA backend specifics (optional, for direct CUDA control) |
| `ggml-vulkan.h` | Vulkan backend specifics |
| `ggml-opt.h` | Optimization passes |
| `gguf.h` | GGUF file format reader/writer |

### 3.3 Multimodal Headers (`llama.cpp/tools/mtmd/`)

| Header | Size | Purpose |
|--------|------|---------|
| `mtmd.h` | 13.2 KB | **Primary multimodal C API** — context init, bitmap creation (image + audio), tokenization, encoding, embedding retrieval |
| `mtmd-helper.h` | 4.6 KB | **Helper functions** — `mtmd_helper_eval_chunks()` (decode text + multimodal in one call), file loading, KV cache tracking |
| `clip.h` | 4.7 KB | CLIP vision encoder (internal to mtmd) |
| `mtmd-audio.h` | 3.9 KB | Audio processing (internal to mtmd) |
| `mtmd-image.h` | 8.8 KB | Image processing (internal to mtmd) |

**Key functions for GemmaStage.dll:**
- `mtmd_init_from_file()` — load mmproj companion file
- `mtmd_support_vision()` / `mtmd_support_audio()` — capability detection
- `mtmd_bitmap_init()` / `mtmd_bitmap_init_from_audio()` — input creation
- `mtmd_tokenize()` — tokenize text + media markers
- `mtmd_helper_eval_chunks()` — decode everything in one call
- `mtmd_helper_bitmap_init_from_file()` — load image/audio from file path

### 3.4 Common Library Headers (`llama.cpp/common/`)

| Header | Purpose |
|--------|---------|
| `common.h` / `common.cpp` | High-level wrappers, parameter parsing |
| `sampling.h` / `sampling.cpp` | Sampler chain setup (temperature, top-k, top-p, grammar) |
| `chat.h` / `chat.cpp` | Chat template application and tool-calling message formatting |
| `json-schema-to-grammar.h` | Convert JSON Schema → GBNF grammar (for constrained decoding) |

> **Note:** The `common` library is built as a static lib when building llama.cpp from source. With prebuilt DLLs, its functionality is partially available through `llama.dll` exports and partially needs to be reimplemented or compiled from source into `GemmaStage.dll`.

---

## 4. Models (`models/`)

### 4.1 Gemma 4 E4B GGUF (`models/gemma-4-E4B/`)

| File | Size | Purpose |
|------|------|---------|
| `gemma-4-E4B-it-Q4_K_M.gguf` | **4,746.60 MB** | Main model weights (Q4_K_M quantization, instruction-tuned) |
| `mmproj-BF16.gguf` | **945.62 MB** | Multimodal projection file (BF16, for vision + audio encoder) |
| `imatrix_unsloth.gguf_file` | 4.22 MB | Importance matrix (used during quantization, not at inference) |
| `config.json` | 5.2 KB | HuggingFace model config (reference only, not used by llama.cpp) |

**Model details (from `config.json`):**
- Architecture: `Gemma4ForConditionalGeneration`
- Text model: 42 layers, hidden_size=2560, 8 attention heads, 2 KV heads
- Vision: 16 layers, patch_size=16, 280 soft tokens per image
- Audio: 12 layers, hidden_size=1024, sample rate dependent on mmproj
- Max position embeddings: 131,072
- Vocab size: 262,144
- Layer types: sliding_attention (window=512) + full_attention (every 6th layer)
- Multimodal tokens: image=258880, audio=258881, eoi=258882, eoa=258883, video=258884

**This is E4B, not E2B.** The implementation plan references E2B (~6.3 GB), but the actual model in `models/` is **E4B** (~4.7 GB at Q4_K_M). E4B is the larger variant with more parameters — better quality but higher VRAM requirement for the base model. The Q4_K_M quantization keeps it manageable.

### 4.2 Legacy LiteRT-LM Models (`models/legacy_litertlm/`)

Both `gemma-4-E2B-it.litertlm` (2.4 GB) and `gemma-4-E4B-it.litertlm` (3.4 GB) with associated XNN/WebGPU caches. **These are retired** — not used by the v5 llama.cpp integration.

---

## 5. Import Library Strategy

### The Problem

The prebuilt distribution ships only runtime DLLs — **no `.lib` import libraries**. On Windows/MSVC, linking against a DLL typically requires an import library (`.lib`) so the linker can resolve symbols at build time.

### Solution: Generate `.lib` from `.dll`

For each DLL we need to link against (`llama.dll`, `mtmd.dll`), we'll generate import libraries using the MSVC tools:

```powershell
# Step 1: Dump exports from the DLL
dumpbin /EXPORTS llama.dll > llama.exports.txt

# Step 2: Create a .def file listing the exports
# (parse from dumpbin output or write manually)

# Step 3: Generate .lib from .def
lib /DEF:llama.def /OUT:llama.lib /MACHINE:X64
lib /DEF:mtmd.def /OUT:mtmd.lib /MACHINE:X64
```

This will be automated in the CMake project (Task 1.1.2). The generated `.lib` files are pure linker stubs — the actual code runs from the DLLs at runtime.

**Alternative approach:** Use `LoadLibrary()` + `GetProcAddress()` for all llama.cpp calls. This avoids needing import libraries entirely but is more verbose. Given the number of functions we call, import libraries are cleaner.

---

## 6. Distribution Strategy Decision

### Chosen: **Single `GemmaStage.dll` + runtime backend selection**

**Architecture:**

```
GemmaStage/                       ← New CMake project (OUTSIDE llama.cpp/)
├── CMakeLists.txt                 ← Builds GemmaStage.dll + test_load.exe
├── src/
│   └── GemmaStage.cc             ← v5 implementation (wraps llama.cpp C API)
├── include/
│   └── GemmaStage.h              ← Public header (GS_* exports)
└── tests/
    ├── test_load.cc              ← Basic load + text inference test
    └── test_poc.cc               ← Audio PoC (continuous conversation)

llama.cpp/                        ← Git submodule (READ-ONLY reference, headers)
├── include/llama.h               ← Compile-time headers
├── ggml/include/                 ← GGML headers
└── tools/mtmd/mtmd.h             ← Multimodal headers

llama_prebuilt/                   ← Prebuilt runtime DLLs (downloaded)
├── llama-b8816-bin-win-cuda-12.4-x64/
├── llama-b8816-bin-win-vulkan-x64/
└── cudart-llama-bin-win-cuda-12.4-x64/

models/
└── gemma-4-E4B/                  ← GGUF model + mmproj
```

### Why single DLL + dynamic backend selection:

1. **ggml backend registry handles it.** At `GS_EngineCreate()` time, the DLL calls `ggml_backend_load_all()` which scans the DLL directory for `ggml-cuda.dll`, `ggml-vulkan.dll`, etc. and loads whichever is available. No compile-time decision needed.

2. **Ship both backends.** The deployment directory contains both `ggml-cuda.dll` *and* `ggml-vulkan.dll`. On machines with NVIDIA GPUs and CUDA runtime, CUDA is used (highest performance). On machines with only Vulkan (AMD, Intel), Vulkan is used. CPU `ggml-cpu-*.dll` variants are always available as fallback.

3. **One DLL to maintain.** `GemmaStage.dll` is compiled once, links against `llama.lib` and `mtmd.lib` (generated import stubs), and runs on any machine. Backend selection is 100% runtime.

4. **Unity integration is simpler.** Drop one `GemmaStage.dll` + the ggml/llama runtime DLLs into `Assets/Plugins/x86_64/`. No per-GPU-vendor builds.

### Runtime deployment directory layout:

```
output/                           ← What CMake produces / what ships to Unity
├── GemmaStage.dll                ← Our thin wrapper
├── llama.dll                     ← llama.cpp core (shared between CUDA/Vulkan)
├── mtmd.dll                      ← Multimodal support
├── ggml.dll                      ← GGML dispatcher
├── ggml-base.dll                 ← GGML base operations
├── ggml-cuda.dll                 ← CUDA backend (optional, ~502 MB)
├── ggml-vulkan.dll               ← Vulkan backend (optional, ~59 MB)
├── ggml-cpu-*.dll                ← CPU backends (auto-selected by arch)
├── ggml-rpc.dll                  ← RPC backend (optional, for distributed)
├── libomp140.x86_64.dll          ← OpenMP runtime
├── cublas64_12.dll               ← CUDA redistributable (if shipping CUDA)
├── cublasLt64_12.dll             ← CUDA redistributable (if shipping CUDA)
├── cudart64_12.dll               ← CUDA runtime (if shipping CUDA)
└── test_load.exe                 ← Test executable
```

### CUDA vs Vulkan — DLLs are interchangeable

`llama.dll` and `mtmd.dll` are **identical** between the CUDA and Vulkan prebuilt archives (same size, same symbols). The only difference is which `ggml-*` backend DLL is included. This means we can:
- Take `llama.dll` + `mtmd.dll` from *either* archive
- Include both `ggml-cuda.dll` and `ggml-vulkan.dll` in the same directory
- ggml loads whichever backend is appropriate at runtime

---

## 7. Header Compilation Strategy

### Problem

`GemmaStage.dll` needs to `#include "llama.h"` and `#include "mtmd.h"` at compile time, but these headers live inside the `llama.cpp/` submodule, which is primarily a reference repo — not something we build.

### Solution

The CMake project will add the submodule's header directories to the include path:

```cmake
target_include_directories(GemmaStage PRIVATE
    ${CMAKE_SOURCE_DIR}/llama.cpp/include          # llama.h
    ${CMAKE_SOURCE_DIR}/llama.cpp/ggml/include      # ggml.h, ggml-backend.h, etc.
    ${CMAKE_SOURCE_DIR}/llama.cpp/tools/mtmd         # mtmd.h, mtmd-helper.h
)
```

We define `LLAMA_SHARED` so that `LLAMA_API` resolves to `__declspec(dllimport)` and `MTMD_API` resolves to `__declspec(dllimport)` — telling the compiler the symbols come from external DLLs.

### Version drift risk mitigation

- Pin the submodule to a commit close to `b8816` (or the exact commit that built the prebuilts, if determinable).
- Only use functions documented in `llama.h` and `mtmd.h` — these are the stable public C API.
- If we hit a symbol mismatch at link time, the fix is either: (a) update the prebuilt, or (b) rebuild llama.cpp from the submodule source with the same CMake.

---

## 8. Key API Functions for GemmaStage.dll

### From `llama.h`:

| Function | Used for |
|----------|----------|
| `llama_backend_init()` | Initialize backend system |
| `llama_model_default_params()` | Get default model params |
| `llama_model_load_from_file()` | Load GGUF model |
| `llama_model_free()` | Free model |
| `llama_context_default_params()` | Get default context params |
| `llama_init_from_model()` | Create context (KV cache) from model |
| `llama_free()` | Free context |
| `llama_model_chat_template()` | Get chat template string |
| `llama_model_get_vocab()` | Access vocabulary |
| `llama_vocab_n_tokens()` | Token count |
| `llama_decode()` | Run inference step |
| `llama_sampler_chain_init()` | Create sampler chain |
| `llama_sampler_init_grammar()` | Grammar-constrained sampling |
| `llama_sampler_sample()` | Sample next token |
| `llama_memory_seq_rm()` | KV cache management |
| `llama_n_ctx()` | Query context size |

### From `mtmd.h` / `mtmd-helper.h`:

| Function | Used for |
|----------|----------|
| `mtmd_init_from_file()` | Load mmproj for multimodal |
| `mtmd_free()` | Free multimodal context |
| `mtmd_support_vision()` | Check vision capability |
| `mtmd_support_audio()` | Check audio capability |
| `mtmd_bitmap_init()` | Create image bitmap |
| `mtmd_bitmap_init_from_audio()` | Create audio bitmap |
| `mtmd_bitmap_free()` | Free bitmap |
| `mtmd_tokenize()` | Tokenize text + media |
| `mtmd_helper_eval_chunks()` | Decode multimodal input (text + vision/audio) |
| `mtmd_helper_bitmap_init_from_file()` | Load media from file path |
| `mtmd_helper_get_n_tokens()` | Track token count for KV cache |

### From `common/` (must be compiled into GemmaStage.dll or reimplemented):

| Module | Used for |
|--------|----------|
| `sampling.h/cpp` | Sampler chain configuration (temperature, top-k, top-p, min-p) |
| `chat.h/cpp` | Chat template application and message formatting |
| `json-schema-to-grammar.h/cpp` | Convert `tools_json` → GBNF grammar |

---

## 9. Risks & Open Questions

| # | Risk | Severity | Mitigation |
|---|------|----------|------------|
| 1 | Submodule HEAD ≠ prebuilt `b8816` → ABI mismatch | Medium | Pin submodule to `b8816`-era commit, or generate .lib from the actual DLLs |
| 2 | No import libraries in prebuilt | Low | Generate `.lib` from `.dll` using `dumpbin` + `lib` (automated in CMake) |
| 3 | `common/` library not in prebuilt DLLs | Medium | Compile needed `common/` source files directly into `GemmaStage.dll` |
| 4 | CUDA runtime (~547 MB) bloats distribution | Low | Ship as optional; ggml falls back to Vulkan/CPU if absent |
| 5 | E4B model (4.7 GB) vs E2B mentioned in plan | Low | E4B is available and better quality; update plan docs. E2B GGUF can be obtained later if VRAM is tight |
| 6 | `mmproj-BF16.gguf` is ~946 MB — large | Low | Required for multimodal; no way around it. BF16 can be quantized if needed |
| 7 | 14 CPU backend DLLs add clutter | Low | Can ship only the 2–3 most common (x64, haswell, skylakex) and let ggml pick |

---

## 10. Upstream Version Tracking

| Component | Version | Source |
|-----------|---------|--------|
| llama.cpp submodule | `4fbdabdc6` (master, untagged) | `llama.cpp/` git submodule |
| Prebuilt DLLs | `b8816` | GitHub Releases: `llama-b8816-bin-win-cuda-12.4-x64.zip` |
| CUDA toolkit | 12.4 | NVIDIA (bundled redistributables) |
| Gemma 4 E4B model | Q4_K_M (unsloth quantization) | HuggingFace / community GGUF |
| mmproj | BF16 | Paired with the Gemma 4 E4B GGUF |

---

## 11. Architecture Diagram

```
┌─────────────────────────────────────────────────────────────────────┐
│                         Repository Root                             │
├─────────────────────────────────────────────────────────────────────┤
│                                                                     │
│  GemmaStage/              ← NEW CMake project (Task 1.1.2)         │
│  ├── CMakeLists.txt        ← builds GemmaStage.dll + test_load.exe │
│  ├── cmake/                                                         │
│  │   └── GenerateImportLib.cmake  ← auto-gen .lib from .dll        │
│  ├── src/                                                           │
│  │   └── GemmaStage.cc    ← GS_* exports wrapping llama.cpp API   │
│  ├── include/                                                       │
│  │   └── GemmaStage.h     ← public C header                       │
│  └── tests/                                                         │
│      ├── test_load.cc      ← sanity test                           │
│      └── test_poc.cc       ← audio PoC                             │
│                                                                     │
│  llama.cpp/                ← git submodule (READ-ONLY)              │
│  ├── include/llama.h       ← compile-time header                   │
│  ├── ggml/include/         ← ggml headers                          │
│  ├── common/               ← source files compiled into our DLL    │
│  └── tools/mtmd/           ← mtmd.h, mtmd-helper.h                │
│                                                                     │
│  llama_prebuilt/           ← prebuilt runtime (NOT in llama.cpp/)   │
│  ├── llama-b8816-bin-win-cuda-12.4-x64/                            │
│  ├── llama-b8816-bin-win-vulkan-x64/                               │
│  └── cudart-llama-bin-win-cuda-12.4-x64/                           │
│                                                                     │
│  models/                                                            │
│  └── gemma-4-E4B/          ← GGUF model + mmproj                   │
│                                                                     │
│  LiteRT-LM/                ← LEGACY (retired, reference only)      │
│                                                                     │
│  investigations/                                                    │
│  └── llama/INVENTORY.md    ← THIS FILE                             │
│                                                                     │
└─────────────────────────────────────────────────────────────────────┘

Build-time dependencies:
  GemmaStage.dll ──compile──► llama.cpp/include/ (headers only)
  GemmaStage.dll ──compile──► llama.cpp/ggml/include/ (headers only)
  GemmaStage.dll ──compile──► llama.cpp/tools/mtmd/ (headers only)
  GemmaStage.dll ──compile──► llama.cpp/common/ (source files, compiled in)
  GemmaStage.dll ──link────► llama.lib (generated from llama.dll)
  GemmaStage.dll ──link────► mtmd.lib (generated from mtmd.dll)

Runtime dependencies:
  GemmaStage.dll ──loads──► llama.dll
  GemmaStage.dll ──loads──► mtmd.dll
  llama.dll ──loads──► ggml.dll ──loads──► ggml-base.dll
  ggml.dll ──loads──► ggml-cuda.dll (if CUDA available)
  ggml.dll ──loads──► ggml-vulkan.dll (if Vulkan available)
  ggml.dll ──loads──► ggml-cpu-{arch}.dll (always, as fallback)
  ggml-cuda.dll ──loads──► cublas64_12.dll, cublasLt64_12.dll, cudart64_12.dll
```

---

## 12. Summary of Decisions

| Decision | Choice | Rationale |
|----------|--------|-----------|
| **Distribution strategy** | Single DLL + runtime backend selection | ggml's backend registry handles CUDA/Vulkan/CPU at runtime |
| **Header source** | From llama.cpp submodule | Submodule provides compile-time headers; prebuilt provides runtime DLLs |
| **Import library** | Generate from DLL at CMake configure time | Prebuilt ships no `.lib`; `dumpbin`+`lib` is standard MSVC practice |
| **GemmaStage.dll location** | `GemmaStage/` (separate folder, NOT inside `llama.cpp/`) | Clean separation; own CMake project independent of llama.cpp build system |
| **`common/` library** | Compile needed sources into GemmaStage.dll | Not available as a prebuilt DLL; need `sampling`, `chat`, `json-schema-to-grammar` |
| **Model** | Gemma 4 **E4B** Q4_K_M (~4.7 GB) + mmproj BF16 (~946 MB) | Already in `models/`; E4B is higher quality than E2B |
| **CPU backend DLLs** | Ship all 14 variants | ggml auto-selects the optimal one for the host CPU |
| **Upstream tracking** | Pin submodule to `b8816`-compatible commit | Reduces ABI mismatch risk between headers and prebuilt DLLs |
