# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

GemmaStage is a VR presentation practice environment where users explain ideas on a virtual stage or in a classroom. An on-device Gemma 4 (via **llama.cpp**) evaluates clarity, logic, and completeness — providing feedback and audience simulation entirely locally. The developer is a strong C# developer, new to GameDev/Unity. Has SteamVR + Unity + PC VR headset.

**Strategy:** Build the AI module (C++ DLL) first, test it standalone, then build the Unity VR experience around it.

Architecture:
- **llama.cpp** (prebuilt DLLs in `llama_prebuilt/`) — runs Gemma 4 on-device. Accepts text, audio, and image input natively (via `libmtmd` + mmproj). Outputs text and tool calls in standard OpenAI format. Runtime backend selection: **CUDA → Vulkan → CPU**.
- **GemmaStage.dll** — Thin C++ DLL wrapping llama.cpp's C API (`llama.h` + `mtmd.h`) with `GS_` prefix. Uses llama.cpp's built-in conversation flow and the model's own chat template for normal request handling. Keeps game logic out of the native layer and stays reusable across projects.
- **Session module** — C# layer on top of `GemmaStage.dll` implementing the live-session architecture from `docs/SESSION_ARCHITECTURE.md`: Perceptor (stateless `AsrConversation` per chunk + stateless `I2tConversation` per image) → CognitiveCoordinator driving per-cycle `IdeaReflector (System1Reactor + System2Reflector) → Inquirer` cycles → post-performance pipeline (TranscriptSummarizer / IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator / Clarification / DeepDive) — every conversation is per-call stateless, with state owned by runtime stores.
- **Unity 6 LTS** — VR game layer. VR-only (OpenXR). Two environments: Stage and Classroom. Unity integrates the session module, handles VR interaction, and presents runtime/final feedback in C#.

**No TTS.** The model outputs text only (plus tool calls). All feedback is displayed visually.

---

## Unity Task Workflow

You have specific MCP server for working with Unity Engine. If it is not available - warn it and do not continue.

### UI Authoring Rule — Always Prefabs
Every reusable UI element (panels, rows, buttons, dialogs, etc.) lives as a prefab under `Assets/GemmaStage/<Feature>/UI/Prefabs/`, never as a one-off scene GameObject. When a row/widget shape is needed a second time, make it a prefab variant of the first instead of duplicating the scene object. If you find yourself about to duplicate an in-scene UI GameObject to author a new one, stop and extract it to a prefab first (`PrefabUtility.SaveAsPrefabAssetAndConnect`). The session-setup setting rows live at `Assets/GemmaStage/Lobby/UI/Prefabs/SettingRow_*.prefab` as the canonical example.

---

## YouTrack Workflow

All development is tracked in the **GemmaStage (GS)** YouTrack project.

### Alignment & Stage Management
- **Starting Work:** When beginning work on an issue, immediately verify its current Stage on the YouTrack board and actualize it (e.g., move to **Development**) before starting implementation.
- **Automatic Transitions:** If a Task enters the **Development** stage, all its parents (Epic, Feature) must also be moved to **Development**.
- **Closing Parents:** When all children of an Epic or Feature are moved to **Done**, the parent must also be moved to **Done**.
- **Missing Issues:** If a new problem is encountered that isn't on the board, create a new issue and link it appropriately.
- **Context:** Before implementing any Task, read all parent issues (Epic/Feature) to fully understand the project context.

### Task Completion Workflow
When finishing a task:
1. Provide a summary of results to the user.
2. Propose a Git commit message in the format: `GS-Number - Message content`.
3. Ask the user if the task can be closed.
4. If approved:
   - Commit the changes with the proposed message.
   - Leave a detailed comment on the YouTrack issue.
   - Move the issue to the **Done** stage.

### Commit Message Rules
- **Never mention Anthropic models, Claude, Claude Code, or any AI assistant in commit messages.** No `Co-Authored-By: Claude ...` trailers, no "Generated with Claude Code" lines, no model names. Commit messages stay strictly about the change itself.

### Environment
- **Operating System:** Windows.
- **Terminal:** PowerShell / Windows Terminal.

---

## Build Commands

### GemmaStage.dll (CMake, links against prebuilt llama.cpp)

```powershell
# From repo root
cmake -S GemmaStage -B GemmaStage/build -DCMAKE_BUILD_TYPE=Release
cmake --build GemmaStage/build --config Release
# Output: GemmaStage/build/Release/GemmaStage.dll
```

Quick smoke test:
```powershell
./GemmaStage/build/Release/test_load.exe `
  --model_path=./models/gemma-4-E4B/gemma-4-E4B-it-Q4_K_M.gguf `
  --mmproj_path=./models/gemma-4-E4B/mmproj-BF16.gguf
```

The CMake project links against prebuilt libraries from `llama_prebuilt/` and copies the required llama.cpp + ggml backend DLLs alongside `GemmaStage.dll` at build time.

> **Constrained decoding** is handled through llama.cpp's built-in chat/sampling stack. No external constraint provider DLL is needed.

---

## Architecture Details

### Tool Calling (Standard OpenAI Format)

The DLL exposes llama.cpp's standard OpenAI tool-calling path: tools are defined in OpenAI format and passed to `GS_ConversationConfigCreate()` as `tools_json`. The DLL:
1. Passes conversation state and tool definitions into llama.cpp's built-in chat handling.
2. Relies on the model chat template and llama.cpp's built-in sampling/parsing support for the normal tool-calling path.
3. Returns standard OpenAI-compatible assistant responses for the caller to consume.

**Tool calling is the session module's internal structured-output channel.** Every role uses tools — `report_audio_observation` (Asr), `report_image_observation` (I2t), `report_retelling` (System1Reactor), `report_main_idea_understanding` (System2Reflector), `report_idea_thesis` (IdeaComprehension), Inquirer concern deltas, Clarification, TranscriptSummarizer MAP/REDUCE, GroundTruthSummarizer MAP/REDUCE, MainIdeaComparator (`report_thesis_comparison` + `report_claim_coverage`), DeepDive (seven per-criterion sub-roles) — see `docs/SESSION_ARCHITECTURE.md` for the per-role tool surface. Each role owns a per-role response parser (one C# class per role under `GemmaStage.Session/<role>/`) that deserializes the OpenAI `tool_calls` envelope into typed records.

**Unity does not see raw tool calls.** The session module surfaces typed C# events (state changes, finalized evaluation, surfaced concerns) on `SessionEvents` up to the Unity layer; the Unity-side game-event dispatcher binds those events to game systems. No tool-call JSON crosses into Unity-side game code.

> Earlier drafts used `audience_reaction` and `ask_question` as canonical *Unity-facing* tool examples; neither exists. Per `docs/GAME_DESIGN.md` §6.7 the only visible audience signal is hand-raise, and hand-raise is wired to unanswered open concerns surfaced by the session layer's `OpenConcerns` event (see `docs/SESSION_ARCHITECTURE.md` §9) — not to a model-emitted tool the game layer would dispatch. The NPC system supports adding richer reactions as a data-only extension.

### Data Flow

```
User speaks / draws / shows slide
  → Unity C# captures audio chunks / board images
  → Session module orchestrates multiple conversations on top of `GS_*`
    (AsrConversation per-chunk, I2tConversation per-image, per-cycle
     System1Reactor + System2Reflector, Inquirer — driven by
     CognitiveCoordinator cycles — then post-performance TranscriptSummarizer /
     IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator /
     Clarification / DeepDive)
  → Each conversation emits tool calls (report_*, reflection, MAP/REDUCE,
    DeepDive sub-roles) parsed inside the session module into typed objects
  → Transcript / image / metrics / live+archived concern state stored in-session
  → End of performance may run Final Q&A while the session stays open;
    after that the module finalizes via the post-performance pipeline
  → Unity receives typed session events (no raw tool-call JSON)
  → UI systems render questions, audience reactions, and results
```

### Why the C API (`llama.h` + `mtmd.h`) not C++

llama.cpp's C API uses opaque pointers and plain C types — no ABI fragility across DLL boundaries, and P/Invoke requires `extern "C"` anyway. The C API provides everything we need: multi-context over a single model, blocking request/response turns, abort callback for cancellation, multimodal eval via `libmtmd`, built-in chat-template handling, and constrained decoding.

### llama.cpp Key API Surface

- `llama_model_load_from_file()` / `llama_model_free()` — load weights once, share across contexts
- `llama_init_from_model()` / `llama_free()` — per-conversation context (KV cache lives here)
- `llama_decode()` — prefill + decode
- `common_chat_templates_init()` / `common_chat_templates_apply()` — built-in model chat-template handling
- `common_sampler_init()` — built-in sampling stack used by the conversation path
- `common_chat_parse()` — built-in response parsing for OpenAI-compatible chat output
- `llama_set_abort_callback()` — cancellation hook, checked between decode steps
- `mtmd_init_from_file()` / `mtmd_helper_eval_chunks()` — multimodal (audio + vision) via mmproj
- `ggml_backend_load_all()` + backend registry — runtime CUDA/Vulkan/CPU selection
- **Path handling:** Use forward slashes on Windows for consistency across subsystems.

### GemmaStage.dll Public C API (Unity P/Invoke surface)

Thin wrapper over llama.cpp with `GS_` prefix. All functions exported with `__declspec(dllexport)` + `extern "C"`. Key groups:
- Engine lifecycle: `GS_EngineSettingsCreate`, `GS_EngineCreate`, `GS_EngineDelete` (loads GGUF + mmproj, selects backend)
- Conversation: `GS_ConversationConfigCreate` (takes `system_message` plain text, `tools_json`, `enable_constrained_decoding`), `GS_ConversationCreate`, `GS_ConversationDelete`
- Input: `GS_ConversationSendText`, `GS_ConversationSendAudio`, `GS_ConversationSendImage`, + file path variants
- Control: `GS_ConversationCancelProcess`
- Response: `GS_JsonResponseGetString` (returns raw JSON including `tool_calls`)
- Benchmark: `GS_BenchmarkInfo*` functions (tokens/sec, TTFT, prefill/decode counts)

**P/Invoke marshaling rules for Unity:**
- All `[DllImport("GemmaStage")]` with `CallingConvention.Cdecl`
- Marshal `bool` as 1-byte (`[MarshalAs(UnmanagedType.U1)]`)
- Marshal `size_t` as `UIntPtr`
- Returned strings are owned by native objects — **Unity must copy immediately** via `Marshal.PtrToStringUTF8()`

---

## Runtime DLL Packaging

All DLLs must be in the same directory at runtime. For Unity: `Assets/Plugins/x86_64/`.

```
GemmaStage.dll                                ← CMake output (thin llama.cpp wrapper)

llama.cpp runtime (from llama_prebuilt/):
  llama.dll                                   ← main llama.cpp library
  mtmd.dll (or equivalent)                    ← multimodal (if shipped separately)
  ggml.dll                                    ← ggml core
  ggml-base.dll                               ← ggml base
  ggml-cpu.dll                                ← CPU backend (always include as fallback)
  ggml-cuda.dll                               ← CUDA backend (optional; loaded if CUDA present)
  ggml-vulkan.dll                             ← Vulkan backend (optional; loaded if Vulkan present)

CUDA runtime dependencies (bundled with the CUDA backend):
  cudart64_*.dll, cublas64_*.dll, cublasLt64_*.dll  ← exact set per llama.cpp prebuilt

Models (StreamingAssets):
  gemma-4-E4B-it-Q4_K_M.gguf                 ← main model
  mmproj-BF16.gguf                           ← vision/audio encoder
```

> **Backend selection is runtime, not build-time.** `ggml_backend_load_all()` registers every backend DLL present in the directory. `GS_EngineCreate` picks CUDA → Vulkan → CPU. Unity can override via `GS_EngineSettingsCreate` for benchmarking or player-side backend choice.

---

## Key Files

| File | Purpose |
|------|---------|
| `docs/CONCEPT.md` | GemmaStage product concept |
| `docs/EVALUATION.md` | Rubric used by the final evaluation |
| `docs/SESSION_ARCHITECTURE.md` | Session layer — live-session conversation orchestration as a separate module on top of the DLL |
| `IMPLEMENTATION_PLAN.md` | Full 8-phase roadmap with timeline and risks |
| `llama_prebuilt/` | Prebuilt llama.cpp + ggml runtime DLLs (CUDA + Vulkan) |
| `GemmaStage/src/GemmaStageConversation.cc` | Conversation flow built on llama.cpp's built-in chat/template path |
| `GemmaStage/src/GemmaStage.cc` | DLL entry points and engine wrapper |
| `GemmaStage/CMakeLists.txt` | CMake build for `GemmaStage.dll` + `test_load.exe` |
| `models/` | Local model cache (gitignored) |
| `investigations/` | Research and design decision docs |

## Models

Pre-downloaded to `models/` (gitignored). GGUF format for llama.cpp:
- `gemma-4-E4B-it-Q4_K_M.gguf` (~5.0 GB on disk in the local model folder) — primary and only target model
- `mmproj-BF16.gguf` — multimodal encoder (required for audio/image input)

---

## Environment & Build Constraints

- **Platform**: Windows x64, VR-only (OpenXR/SteamVR)
- **C++ Standard**: C++17
- **Build system**: CMake (Visual Studio 2022 generator).
- **GPU**: CUDA (preferred on NVIDIA) or Vulkan (everywhere else) via llama.cpp's ggml backends. CPU fallback available.
- **VRAM budget** (post-GS-115 stateless refactor + GS-96 slot semaphore): E4B Q4_K_M weights (~5.0 GB on disk in the local model folder) + 1× KV at 8192 ctx (~0.25–0.4 GB; every role — Asr / I2T / System1Reactor / System2Reflector / Inquirer / TranscriptSummarizer / IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator / Clarification / DeepDive — uses `SessionConfig.ContextSize`, and the engine slot semaphore on `EngineHandle` keeps exactly one `llama_context` resident at any moment) + mmproj (~0.3–0.5 GB; loaded at engine creation when `SessionConfig.MmprojPath` is set, resident for the engine lifetime — not unloaded between live and post-performance phases) + VR rendering (~1–2 GB) ≈ **~7.5 GB total typical, live and post-performance**. Fits 12GB+ cards comfortably; 8GB cards are workable. See `docs/SESSION_ARCHITECTURE.md` §14 for the residency model.

---

## GemmaStage-Specific Unity Architecture (Phase 3+)

### Environments
Two scenes: **Stage** (large audience, podium, dramatic lighting) and **Classroom** (smaller room, desks, whiteboard). Both share the same functional components.

### Boards
- **Drawing Board:** VR whiteboard with 3-4 colored markers (XR Grab Interactable). Drawing happens when the marker tip enters an asymmetric proximity band of the surface (no Physics raycast / no surface collider — `DrawingBoard.TryWorldToUVWithProximity` does the projection). Strokes are painted into a CPU `Texture2D` (`Color32[]` flushed once per dirty frame in `LateUpdate`) so capture is a direct `Texture.EncodeToPNG()`.
- **Presentation Board:** Displays pre-loaded images/PDF pages. Next/prev navigation. Files loaded in session settings.

### Board Capture ("Pay Attention")
**One floating "Pay Attention" button per board** (so two total). Press → capture board state as PNG (`Texture.EncodeToPNG()` direct on the drawing board's CPU texture; `Graphics.Blit` + `ReadPixels` + `EncodeToPNG()` for the GPU-resident slide) → `BoardCapture.OnCaptureRequested(BoardKind, byte[])` → Phase 6.6 routes the bytes to `GS_ConversationSendImage` so the model can incorporate the visual. The Whiteboard's Undo button and Presentation Board's Prev/Next buttons use the **same input model** — floating UGUI Canvas widgets pressed via the active-hand ray (GAME_DESIGN §5.1). Drawing markers are the only board-side element grabbed and used directly without the ray.

### Session Flow
**Lobby** (Session Setup Panel: env, duration, Live Q&A + disturbance level, Final Q&A, Ground Truth Doc, presentation files, echo / Game Settings Panel: vignette, turning mode, microphone, language, master audio volume, watch hand) → **Start Session** loads the chosen env → **Performance** (speaking + drawing + slides) → Session module runs Perceptor + periodic CognitiveCoordinator cycles (System1Reactor → System2Reflector → Inquirer) during the live phase → End Session confirmation → optional Final Q&A (Clarification step + sequential questions) → Evaluation progress panel (TranscriptSummarizer MAP/REDUCE → IdeaComprehension → Ground Truth Doc processing → MainIdeaComparator → DeepDive) → Results screen → PDF export option → return to Lobby.

### Audience NPCs
Mixamo humanoids with two reactions at launch: **Idle** (looping) and **HandRaise** (one-shot). The animation system uses an enum + Animator-state-name + `CrossFadeInFixedTime` mapping, so adding more reactions later (nodding, confused look, etc.) is a data-only change. HandRaise is triggered when the session layer emits `OpenConcerns` with a non-empty set after a CognitiveCoordinator cycle (see `docs/SESSION_ARCHITECTURE.md` §9); per `docs/GAME_DESIGN.md` §6.7, hand-raise is the only visible audience signal at launch.

---

## Risks & Mitigations

| Risk | Mitigation |
|------|------------|
| Gemma 4 audio support in pinned llama.cpp version | Verify explicitly before assuming; may require pinning newer llama.cpp or building from source. |
| Gemma 4 E4B image quality via mmproj | Test with whiteboard-style images. |
| Backend selection on user machines (CUDA vs Vulkan vs CPU) | ggml backend registry + dynamic loading. Ship both CUDA and Vulkan backend DLLs; let ggml pick. Unity override available. |
| Inference starves VR frame rate | Benchmark on both backends. May need to throttle inference rate or switch backends under load. |
| Constrained decoding overhead | Benchmark with and without it. Keep the default path aligned with llama.cpp built-ins and enable stricter constraints where needed. |
| Context window exhaustion | `n_ctx` is user-configurable in llama.cpp. Mitigation: raise `n_ctx`, or summarize. |
| Tool call output quality from E4B model | Use the model chat template plus llama.cpp built-ins first. |
| P/Invoke bridge + IL2CPP | Keep the Unity bridge thin, verify the blocking request/response path first, and add callback interop only when UX needs it. |
| VR drawing board accuracy | Asymmetric proximity band (front/back tolerance in surface-local Z) is the tuning knob — tight on the approach side, loose on the penetration side. Start simple (flat surface, large board, thick markers). |
| PDF rendering in Unity | Convert PDF pages to images at load time. |
| Prebuilt DLL / CUDA runtime mismatch on user machines | Bundle only DLLs from llama.cpp prebuilt's toolchain; include required CUDA redistributables. Test on a clean Windows VM. |
| Real-time feedback latency | llama.cpp first-token latency on CUDA is strong; benchmark under load. |

---

## Phases Overview (post Phase 1)

**Phase 2** — Session Architecture Module + Audio-Chunking PoC: standalone session-layer module on top of the DLL (Perceptor / per-cycle System1Reactor + System2Reflector / Inquirer / Clarification / TranscriptSummarizer / IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator / DeepDive per `docs/SESSION_ARCHITECTURE.md`), volume-based `.wav` chunker (X/Y/C parameters), end-to-end console PoC driven by TED talk audio with full per-chunk logs and timing/VRAM metrics.

**Phase 3** — Unity 6 LTS VR project. OpenXR enabled from start. Two environments (Stage, Classroom). Audience NPCs with reactive animations.

**Phase 4** — VR Interaction: Drawing board with VR markers (with a floating Undo button), Presentation Board (with floating Prev/Next buttons), floating "Pay Attention" capture buttons (one per board). All four floating buttons are pressed via the active-hand ray.

**Phase 5** — Session Management: Lobby scene with two world-space panels (Session Setup + Game Settings), setup→performance transition, `RayController` for performance-phase ray gating (manual A/X toggle + far-panel auto-ray on End Session / Evaluation / Results), wrist timer, End Session confirmation flow, separate Live Q&A and Final Q&A flows, echo postprocessing, Evaluation progress panel, and system-prompt wiring to the session module.

**Phase 6** — AI Integration: `GemmaStageNative.cs` (P/Invoke), `GemmaStageService.cs` (DLL/threading boundary), session-module integration on top of `GS_*` (per-role response parsers already live in `GemmaStage.Session/`), `GameEventDispatcher.cs` (routes typed `SessionEvents` to game systems), audio pipeline, board capture pipeline.

**Phase 7** — Results & Polish: evaluation results screen, PDF export, visual/audio polish, 90fps VR optimization, lighting bake for both env scenes (deferred from Phase 3), Affordance Callouts (controller binding hints), final cleanup of XRI sample reference scenes.

**Phase 8** — Build & Ship: model bundling (GGUF + mmproj in StreamingAssets), first-run model verification, Windows standalone build. **Out of scope:** session save/load, mic selection UI, manual backend-override UI. Comfort/Language settings are persisted in Phase 5.
