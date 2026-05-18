# GemmaStage — Implementation Plan

**Goal:** A VR presentation practice environment where users explain ideas on a virtual stage or in a classroom, and an on-device Gemma 4 evaluates clarity, logic, and completeness — providing feedback and audience simulation entirely locally.

**Your profile:** Strong C# developer, new to GameDev/Unity. Have SteamVR + Unity + PC VR headset ready.

**Strategy:** Build the AI module (C++ DLL) first, test it standalone, then build the Unity VR experience around it.

---

## Why llama.cpp

llama.cpp is the inference backend, providing everything GemmaStage needs:

- **Gemma 4 E4B support**, with multimodal input via `libmtmd` (vision + audio through the mmproj companion file). GemmaStage targets E4B only.
- **VRAM headroom on consumer GPUs.** With an 8 K engine context window, Q4_K_M weights, one resident KV cache, mmproj, and the VR render heap, the live budget sits around ~7 GB total — comfortable on 8 GB+ cards.
- **Multi-context per loaded model.** Many `llama_context`s can share one `llama_model`, so future dual-conversation or parallel-context designs remain open.
- **Desktop GPU throughput** via first-class CUDA *and* Vulkan backends, with CPU as a fallback. Backend selection is dynamic at runtime through ggml's backend registry.
- **Standard OpenAI-format tool calling** via the model chat template, with grammar-based constrained decoding (GBNF / JSON-schema), cancellation, and benchmarking built in.

---

## Architecture Overview

```
┌──────────────────────────────────────────────────────┐
│      GemmaStage.dll  (Thin C++ Wrapper)              │
│                                                      │
│   Wraps llama.cpp C API (libllama + libmtmd).        │
│   Public surface uses the GS_ prefix:                │
│     Engine / Conversation lifecycle                  │
│     SendText / SendAudio / SendImage (+ streaming)   │
│     Cancel, Benchmark                                │
│     Conversation config: system prompt, tools JSON,  │
│       llama.cpp built-in chat/template flow          │
│                                                      │
│   Runtime backend selection (via ggml backend        │
│   registry): CUDA → Vulkan → CPU fallback.           │
│                                                      │
│   Keeps the native layer thin and focused on         │
│   inference/runtime concerns, not game logic.        │
│   Uses llama.cpp built-ins plus the model chat       │
│   template for the normal conversation path.         │
└──────────────────────┬───────────────────────────────┘
                       │ P/Invoke
┌──────────────────────▼───────────────────────────────┐
│                   UNITY (C#) — VR Only               │
│                                                      │
│   P/Invoke bridge ── synchronous, caller-thread      │
│     blocking; no internal threading                  │
│   Session module ── multi-conversation orchestrator  │
│     (Asr / I2T / IdeaReflector split into            │
│      System1Reactor + System2Reflector / Inquirer /  │
│      Clarification / TranscriptSummarizer /          │
│      GroundTruthSummarizer / IdeaComprehension /     │
│      MainIdeaComparator / DeepDive); an engine slot  │
│      semaphore enforces a single live llama_context  │
│   Typed session events ── perceptor turn,            │
│     cognitive cycle, open concerns, transcript       │
│     summarizer, ground truth, comparator,            │
│     clarification, deepdive, ended                   │
│   Game event dispatcher ── routes session events     │
│     to game systems (no raw tool calls cross over)   │
│   Session lifecycle ── settings, timer, transitions  │
│   Audio pipeline ── mic capture → send to DLL        │
│   Input manager ── VR controllers (OpenXR)           │
│   Environments: Stage / Classroom                    │
│   Drawing board ── VR markers, capture to image      │
│   Presentation board ── pre-loaded images/PDFs       │
│   Audience system ── reactive NPCs                   │
│   Results screen ── evaluation display + PDF export  │
└──────────────────────────────────────────────────────┘
```

**Key architectural decisions:**

- The DLL stays thin and reusable — a generic LLM inference wrapper, not GemmaStage-specific.
- The Unity-facing C API is the `GS_*` surface; the C# P/Invoke layer mirrors it 1:1.
- Backend selection is transparent to Unity. At engine creation the DLL asks ggml's backend registry which accelerators are present and picks the best one (CUDA > Vulkan > CPU). Unity can pin a backend via `GS_EngineSettingsCreate` if needed.
- Tool calling emits standard OpenAI-format JSON via llama.cpp built-ins and the model chat template.

**Key points:**

- Gemma 4 accepts text, audio, and image input natively via `libmtmd` — no separate STT needed.
- Model outputs text and function calls in standard OpenAI tool-calling format.
- No TTS — all model output is text displayed on screen.
- During a performance, conversation history is hidden from the user; only the final evaluation is presented.
- The DLL is a generic LLM inference wrapper — reusable in other projects.
- Tool-call parsing happens inside the session module (C#); Unity game systems consume typed session events, never raw tool calls.
- All processing is on-device — "your ideas stay yours".

---

## Data Flow

```
User speaks / draws / shows slide
  ↓
Unity C# (Phase 6 wrapper around the session module)
  → Captures audio chunks / board images on a worker thread
  → Pushes them into the session module (blocking calls)
  ↓
Session module (docs/SESSION_ARCHITECTURE.md)
  → Drives multi-conversation orchestration on top of the GS_* C API:
    Asr (per-chunk), I2T (per-image), IdeaReflector cycle
    (System1Reactor + System2Reflector), Inquirer,
    plus Clarification (Revise + Resolve) and the Final Q&A round
    transitions, then Summarizer, IdeaComprehension,
    GroundTruthSummarizer, MainIdeaComparator, DeepDive
  → Per-role response parsers deserialize the OpenAI tool_calls
    envelope into typed records
  → Updates the live concern store / concern archive + transcript /
    image / metric stores
  → Raises typed session events
  ↓
Unity main thread (Update)
  → Binds session events to game systems (no raw tool-call JSON crosses
    this boundary — Unity sees typed events only):
    Cognitive cycle completed (Live Q&A enabled, newly-open concern detected)
        → audience NPC plays HandRaise
          + Q-mark above the NPC
          + floating "Can I ask a question?" panel showing the question
          (audience visibly only ever Idles or HandRaises per GAME_DESIGN §6.7;
           one popup is active at a time per GAME_DESIGN §6.1)
    DeepDive completed
        → Results screen (Phase 7.1)
```

---

## DLL Behavior

`GemmaStage.dll` wraps llama.cpp's C API (`llama.h` + `mtmd.h`) behind the `GS_*` export surface. It adds convenience helpers (message handling for text/audio/image inputs, blob decode for Unity payloads, path normalization) while leaning on llama.cpp's built-in chat/template path for normal conversation handling.

### Engine and Conversation Model

- `GS_EngineCreate` → loads the GGUF model into a `llama_model` (shared weights), initializes the ggml backend registry, picks CUDA/Vulkan/CPU. Optionally loads an mmproj companion file into an `mtmd_context` for multimodal input.
- `GS_ConversationCreate` → creates a `llama_context` (KV cache) tied to that model and initializes the built-in llama.cpp chat/template support used by the conversation path.
- `GS_ConversationSendText / SendAudio / SendImage` → builds the turn through llama.cpp's built-in chat handling and the model chat template, then runs text or multimodal evaluation and returns the completed assistant response.
- `GS_ConversationCancelProcess` → sets a cancellation flag checked between decode steps (llama.cpp's abort callback).

Multiple `llama_context`s can share one `llama_model` — so multi-conversation layouts cost only extra KV cache, not extra copies of the weights. The live session layer (see `docs/SESSION_ARCHITECTURE.md`) uses this as a separate module on top of the DLL.

### Tool Calling

The DLL exposes llama.cpp's standard OpenAI tool-calling path: tools are defined in OpenAI format and passed to `GS_ConversationConfigCreate()` as `tools_json`. Under the hood the DLL:

1. Passes tool definitions into llama.cpp's built-in conversation/template flow.
2. Lets the model chat template and llama.cpp built-ins drive tool-calling behavior.
3. Returns OpenAI-compatible assistant responses that the caller deserializes.

**Tool calling is the session module's internal structured-output channel.** Every role uses tools to shape its response — `report_audio_observation` (Asr), `report_image_observation` (I2t), plus `report_retelling` (System1Reactor), `report_main_idea_understanding` (System2Reflector), `report_idea_thesis` (IdeaComprehension), Inquirer reflection, Clarification, Summarizer MAP/REDUCE, GroundTruthSummarizer MAP/REDUCE, MainIdeaComparator (`report_thesis_comparison` + `report_claim_coverage`), and DeepDive evaluation. The session-module C# layer parses those tool calls into typed objects. See `docs/SESSION_ARCHITECTURE.md` for the per-role tool surface.

**Unity never sees raw tool calls.** The session module emits typed C# events (state changes, finalized evaluation, surfaced concerns) up to the Unity layer; game systems bind to those events. Tool-call JSON does not cross the session-module / Unity boundary.

> Per GAME_DESIGN §6.7 the only visible audience signal is hand-raise, and it is wired to unanswered open concerns surfaced by the session layer's cognitive cycle — not to a model-emitted tool the game layer would dispatch. The NPC system supports adding richer reactions as a data-only extension.

### Constrained Decoding

llama.cpp has first-class constrained decoding support. GemmaStage keeps this aligned with llama.cpp's built-in chat and sampling flow rather than describing it as a separate custom subsystem.

### Conversation Architecture

The DLL exposes generic `Conversation` primitives — create / send / cancel / delete. How a session uses them (one conversation, several conversations, lifecycle, prompt design) is not the DLL's concern.

Session-level conversation orchestration lives in a separate module documented in `docs/SESSION_ARCHITECTURE.md`. The DLL doesn't need to know about it; any module can be plugged on top so long as it speaks the `GS_*` API.

`n_ctx` is configurable per conversation — the DLL imposes no fixed window choice.

---

## PHASE 1: C++ Module — GemmaStage.dll

*Build and test the thin llama.cpp wrapper DLL.*

### 1.1: llama.cpp Integration & DLL Build

Set up a CMake project that links against the prebuilt llama.cpp libraries in `llama_prebuilt/` (CUDA + Vulkan variants), produces `GemmaStage.dll` exporting the `GS_*` C API, and verifies every input modality (text, audio, image), cancellation, tool calling with constrained decoding, and runtime backend selection.

CMake is sufficient — llama.cpp ships prebuilt DLLs, so no source build is required.

### 1.2: Single Conversation Audio PoC

Build `test_poc.exe` — a proof-of-concept that validates continuous audio inference in a single long-lived conversation. The PoC iteratively sends `audio_sample.wav` to the conversation in a loop until interrupted (Ctrl+C), simulating the real GemmaStage workload of continuous audio processing. No mic capture, no text input, no images — just the core audio inference loop to validate stability, KV cache growth, and performance over time. Real-time debug output shows tokens/sec, VRAM, and GPU utilization. Run alongside an open Unity VR game to test GPU contention — now on both CUDA and Vulkan backends.

---

## PHASE 2: Session Architecture Module + Audio-Chunking PoC

*Build the session layer described in `docs/SESSION_ARCHITECTURE.md` on top of the DLL, and validate it end-to-end with recorded TED audio — before any Unity work.*

### 2.1: Session Module

Implement the Perceptor / IdeaReflector (System1Reactor + System2Reflector) / Inquirer / Clarification / TranscriptSummarizer / IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator / DeepDive flow per `docs/SESSION_ARCHITECTURE.md` as a standalone module on top of the DLL. Includes the Perceptor split (per-chunk audio role + per-image image role), the per-cycle IdeaReflector split (System1Reactor produces topic + retelling without thinking; System2Reflector integrates into a cumulative plain-text `main_idea_understanding` capped at 250 words with thinking on — every distinct claim preserved, wording compressed), Inquirer with cognitive-cycle scheduling, On-Live and Final Q&A round transitions (with restricted Inquirer for the latter), runtime-owned concern state / archive / retellings history, the two-stage Clarification (Revise + Resolve) at the head of Final Q&A, transcript-only Summarizer map-reduce (with the MAP split into a retelling pass + a signals pass per chunk; REDUCE produces an inferred main idea capped at 250 words), the one-shot IdeaComprehension call that finalizes the audience-side thesis (≤ 50 words) from the cumulative `main_idea_understanding`, optional GroundTruthSummarizer (MAP per chunk emits flat new claims with the prior accumulator as context; REDUCE consolidates to thesis ≤ 50 words plus a newline-separated claims plain text ≤ 250 words), GT-required MainIdeaComparator (one thesis-comparison call + one per-claim coverage call asking whether the audience paragraph supports each anchor claim), and final DeepDive evaluation in a WithAnchor / NoAnchor variant. Four DeepDive sub-roles (Structure, Consistency & Focus, Support & Justification, Language Quality) have their `value` computed deterministically by the runtime from chunk-label distributions and overridden post-hoc; Main Idea Clarity receives a suggested score derived from Recall but is not overridden. No UI, no VR — pure library on top of the `GS_*` API. Tool calls are used as the internal structured-output channel (see "Tool Calling" above and SESSION_ARCH for the per-role surface).

### 2.2: Volume-Based Audio Chunker

A component that takes a `.wav` file and emits audio chunks for the session module. Parameters:

- **X** — minimum useful speech window counted in `V` symbols only (e.g. 4s)
- **Y** — trailing silence, in milliseconds, that triggers chunk emission after X is satisfied (default 300ms in the PoC)
- **C** — classification granularity and padding unit in milliseconds (e.g. 1000ms)

Approach: classify each `C`-wide window as silence (`o`) or volume content (`V`), accumulate voice islands plus C-padding into a stitched chunk buffer, and emit when `Y` milliseconds of continuous silence have elapsed after X useful voice symbols are gathered. Long internal silence is compacted to boundary padding: at most one trailing `o` after the previous voice island and one leading `o` before the next voice island are kept.

Worked EXAMPLE (each symbol = 1s, X=4s, Y=2000ms, C=1000ms): input `oooooVVoVoooVoooVVoo` → two chunks `[oVVoVooVo]`, `[oVVo]`.

### 2.3: End-to-End PoC with TED Audio

Console test app that drives the full pipeline without VR:

- **Input:** path to a TED talk `.wav`
- **Pipeline:** wav → chunker → session module → logs
- **Logs (per chunk):** Perceptor structured outputs (Asr / I2t), System1Reactor retellings + System2Reflector `main_idea_understanding` snapshots, Inquirer reflections, Clarification Revise + Resolve outputs, TranscriptSummarizer MAP/REDUCE outputs, IdeaComprehension thesis, GroundTruthSummarizer (per-chunk claims + REDUCE thesis/claims) and MainIdeaComparator (thesis comparison + per-claim coverage) records, final DeepDive evaluation
- **Metrics:** timing, token counts, VRAM across the run

Purpose: debug the session flow end-to-end without VR in the loop, validate constrained-decoding schemas on real speech, tune chunker parameters, and check the single-KV memory model under real workloads.

---

## PHASE 3: Unity VR Project Setup

*Set up Unity as a VR-first project with the core environment framework.*

### 3.1: Project & VR Foundations

Create a Unity 6 LTS project using the 3D (URP) template. Install and enable OpenXR from the start (this is VR-only, not VR-ready-later). Set up XR Interaction Toolkit for controller input, hand tracking, continuous locomotion, and snap turning. **Teleport is out of scope** — see GAME_DESIGN §4.2.1.

### 3.2: Two Environments — Stage and Classroom

Build two environment scenes:

**Stage:** A presentation stage with a podium area, audience seating, dramatic lighting. The presenter (player) stands at the front. Drawing board and presentation board positioned nearby.

**Classroom:** A teaching room with desks, a front teaching area, whiteboard/board area. More intimate setting. Same board setup but in a classroom context.

Both environments share the same functional components (boards, audience, session logic) but with different visual layouts and atmosphere.

### 3.3: Audience NPCs

Import rigged humanoid characters (Mixamo). Ship with **two reactions: Idle (looping) and HandRaise (one-shot)** — matching GAME_DESIGN §6.7 (hand-raise is the only visible audience signal at launch). The animation system uses an enum + Animator state-name + cross-fade mapping, so adding more reactions later (nodding, confused look, attentive lean, etc.) is a data-only change — drop in a clip, name the state to match the enum value, no code change in the audience NPC or audience manager.

The audience manager triggers reactions from signals produced by the session layer — see `docs/SESSION_ARCHITECTURE.md`. The HandRaise trigger specifically is an unanswered open concern surfaced by a cognitive cycle.

Stage and Classroom both seat ~20 NPCs.

---

## PHASE 4: VR Interaction — Boards and Capture

*Build the core VR interaction: drawing boards, presentation display, and board capture.*

### 4.1: Drawing Board with VR Markers

A physical whiteboard/blackboard in the VR scene. 3–4 colored markers sitting on a tray that the player can grab with VR controllers (XR Grab Interactable). Drawing happens when the marker tip enters an asymmetric proximity band of the board surface — a UV-with-proximity projection on the board projects the tip into surface-local space and returns the UV when the band + UV bounds are satisfied (no Physics raycast, no surface collider). **Floating Undo button** positioned next to the whiteboard — removes the last stroke, pressed via the active-hand ray (GAME_DESIGN §5.1).

The board content is painted into a CPU texture flushed once per dirty frame so it can be captured as a PNG directly.

### 4.2: Presentation Board

A second board (screen/projector) that displays pre-loaded images or PDF pages. **Floating Prev/Next buttons** positioned on the user-facing side of the slide quad, pressed via the active-hand ray (GAME_DESIGN §5.1). PDF pages rendered as textures. Files are loaded during Lobby session setup (not mid-presentation).

### 4.3: Board Capture — "Pay Attention" Buttons

Per GAME_DESIGN §5.1: **one floating "Pay Attention" button per board** (so two in the scene total) — one near the Whiteboard, one near the Presentation Board. Pressed via the active-hand ray (manually toggled on with A/X) or by finger poke when the hand is near the button. When pressed:
1. Captures the current board state as a PNG. The drawing board uses direct CPU-side encoding; the presentation slide goes through a GPU readback before encoding.
2. Raises a typed board-capture event (board kind + PNG bytes). Phase 6.6 wires that event into the inference service so the bytes flow to `GS_ConversationSendImage`.
3. The model references the visual content in its evaluation.

> Same input model as the **Whiteboard Undo button and Presentation Board Prev/Next buttons** (also Phase 4) — all four are floating UGUI Canvas widgets pressed via the active-hand ray (GAME_DESIGN §5.1). Drawing markers are the only board-side element grabbed and used directly without the ray.

This is how the multimodal reasoning works in practice — the user draws a diagram, presses the button, and the model incorporates the visual into its understanding.

---

## PHASE 5: Session Management

*Lobby scene, world-space UI panels (Session Setup + Game Settings), setup → performance transition, ray-mode plumbing, wrist timer, End Session flow, Live + Final Q&A, echo postprocessing, and the Evaluation progress panel.*

### 5.1: Lobby Scene

A neutral scene (soft skybox, plain floor, no audience, no boards) that the player spawns into before any session — per GAME_DESIGN §4.1. Stage and Classroom load only on **Start Session**.

`Bootstrap.unity` is the startup scene. It contains only a tiny loader that additively loads `Shared`, then `Lobby.unity`, sets `Lobby` active, and unloads `Bootstrap`. `Shared.unity` holds the XR rig and the persistent runtime services, including `GameManager`, `EnvironmentManager`, and `SessionManager`. `Lobby`, `Stage`, and `Classroom` stay scene-local. The Phase 3 `defaultEnvironment` field on `GameManager` is retained as an editor-only shortcut for dev iteration; in shipping behavior it is unused.

### 5.2: Session Setup Panel (left, primary)

The primary world-space panel in the Lobby. Holds settings scoped to the upcoming session.

- **Environment** — expandable dropdown: **Stage** (default) / Classroom. Start Session is disabled if the selection is cleared.
- **Performance Duration** — `−` / `+` buttons, range 4–30 min, default 10 min, step 2 min.
- **Live Q&A** — on (default) / off toggle. When on, an adjacent **Disturbance Level** dropdown chooses Low / **Middle** (default) / High, mapped to the per-concern probabilities 0.15 / 0.30 / 0.60 applied Unity-side before surfacing a popup.
- **Final Q&A** — on (default) / off toggle.
- **Ground Truth Doc** *(optional)* — single-file picker (`.txt`, `.md`, `.png`, `.jpg`, `.pdf`). Shows filename + `×` to remove. The Lobby validates/loads the attachment immediately; PDFs are rasterized at attach time and the first page texture is retained as a Unity attachment artifact. During Evaluation (5.11), `SessionLayer` maps the attachment into the module-side `GroundTruthDocInput` contract: text/Markdown stay path-based, while images and PDF page 1 are passed as rasterized image bytes.
- **Presentation** — "N slides attached" indicator + **Attach** button → opens the Presentation Picker popup: scrollable thumbnail list, **Add file** (`.pdf` / `.jpg` / `.png`), `×` per item to remove, `×` on the popup header to close. PDF files auto-expand to one slide per page. Slides are rendered to textures at attach time so unrenderable PDFs surface immediately and the picker shows real previews.
- **Echo** — toggle (off by default). When on, an **Echo Level** slider becomes active. Routes the player's mic back through the headset with ~15 ms delay and slight reverb. The Echo Level control is independent of Master Audio Volume, while the resulting playback still follows the listener volume at output.
- **Start Session button** — triggers the setup → performance transition (5.4). Disabled when no microphone is available; hover/focus shows a short explanatory tip popup.

### 5.3: Game Settings Panel (right, secondary)

The secondary world-space panel in the Lobby. Holds non-session-scoped settings persisted across runs.

- **Comfort Vignette** — on (default) / off. Drives the Tunneling Vignette wired into the rig.
- **Turning Mode** — Off (default) / Snap. Off disables the snap turn provider; Snap enables `SnapTurnProvider`.
- **Microphone** — dropdown populated from `Microphone.devices`. An adjacent horizontal volume meter shows the active microphone's real-time RMS level (sampled from a short monitoring `AudioClip`).
- Every row in both Lobby panels can surface a short tip popup on hover/focus explaining what the setting does. The same popup system is reused for disabled Start Session explanations.
- **Language** — English (default) / French / German / Spanish / Turkish / Italian. Passed to the session layer when it configures the module session.
- **Master Audio Volume** — slider driving `AudioListener.volume`. Affects background music (added in Phase 7), UI sounds (clicks, beeps), and echo playback.
- **Watch Hand** — Right (default) / Left. Controls which controller anchor parents the wrist timer canvas (5.6).

**Persistence:** all six values are saved to `PlayerPrefs` on change and reloaded on next launch. Sessions/performances themselves are not persisted (see Phase 8 "out of scope").

### 5.4: Setup → Performance Transition

`SessionManager` (MonoBehaviour, lives in `Shared`) owns the transition. On **Start Session**:

1. **Loading overlay shown** — blacks out the world before any scene swap so the player never sees NPCs or geometry popping in.
2. `EnvironmentManager.LoadEnvironment(selectedEnv)` — unloads Lobby, loads the chosen env, teleports the XR Rig to its spawn point.
3. **NPC spawner awaited** — overlay stays up while the NPC spawner finishes spawning all audience NPCs (60 s hard timeout before entering the env with a partial audience).
4. Slides handed to `PresentationBoard` (§4.2).
5. `RayController.SetMode(Performance)` — see 5.5. Overlay is still visible, so the mode switch is invisible to the player; it is done early so performance-mode B/Y bindings are active before the loading phase ends.
6. `SessionLayer.Begin(unitySessionConfig, gameSettings)` — **LLM model loaded under the overlay.** `Begin` returns `Task<bool>` that completes once `Session.Start(moduleConfig)` finishes on the worker thread. Audio capture has not started yet; no inference runs. Inside `Begin`, the wrapper maps Unity state into the module-side `GemmaStage.Session.SessionConfig` (model path, mmproj, language, time limit, Q&A toggles, and `GroundTruthDocInput`) and calls `Session.Start(moduleConfig)`. The session module constructs its role conversations internally. For Ground Truth, the wrapper passes either a file path or rasterized image bytes; the session module normalizes that input during the post-performance pipeline. See `docs/SESSION_ARCHITECTURE.md`.
7. **Loading overlay hidden.**
8. **"Ready to begin?" popup shown** — world-space far panel in front of the player with a single **Start Speech** button. A far-panel ray hold is active for the popup's lifetime (same mechanism as the End Session panel — see 5.5 and 5.9) so the active-hand ray is forced on and the player can press the button. No audio is flowing; the wrist timer has not started.
9. Player presses **Start Speech**. Far-panel ray hold released; ray returns to performance-mode default (off until A/X).
10. `AudioPipeline.StartCapture(selectedMic)` — opens capture with the chosen device for the session runtime.
11. `EchoProcessor.Apply(echoEnabled, echoLevel)` — see 5.10.
12. Seed `LiveQaController` and `FinalQaController` — they must be ready for session-module events before any audio reaches the module.
13. `WristTimer.StartCountdown(durationMinutes)` — **last**, see 5.6. The start beep marks "the model is now listening and mic is live", not "we finished loading".

On **End Session** (5.9): notify the session module, run Final Q&A if enabled (5.8), then proceed to the Evaluation phase (5.11).

> Naming: `GameManager.LoadEnvironment` / `UnloadEnvironment` (Phase 3) handle scene swaps only. `SessionManager.StartSession` / `EndSession` handle the full session lifecycle, of which scene loading is one step.

### 5.5: Ray Mode Controller

`RayController` (MonoBehaviour on `XRRig`) gates ray visibility per phase, layered on top of the Phase 3 `ActiveHandController`:

- **Lobby mode:** ray always on, follows active hand.
- **Performance mode:** ray off by default. Two enable paths:
  - **Manual toggle** — **below face button** (A on right / X on left) claims the active hand and controls the ray: same-hand press toggles on/off; other-hand press claims that hand and turns the ray on (keeps it on if already on).
  - **Far-panel auto-ray** — far floating panels (End Session §5.9, Evaluation §5.11) show the ray on the current active hand while visible, then return to normal performance-mode ray behavior on close. Near panels (Live Q&A §5.7, Final Q&A §5.8) sit close enough for finger poke and leave the ray untouched.
- The **upper face button** (B on right / Y on left) is reserved for the End Session flow (5.9) and is not wired to the ray.

### 5.6: Wrist Timer

A wrist-mounted UI (TextMeshPro on a small world-space canvas) parented under the controller anchor selected in Game Settings (§4.4.6). The `WristTimer` component reparents on watch-hand change.

- Counts down from `durationMinutes`. Display format: `MM:SS`.
- **Three audio cues** (all played from an `AudioSource` on the timer canvas):
  - Session start — regular beep.
  - 1 minute remaining — regular beep.
  - Time-out — **distinct** beep (different clip).
- At time-out: enters **penalty mode**. Display flips to count-up, digits render in dark red. Session continues; the timer never ends the session itself.

### 5.7: Live Q&A

Per GAME_DESIGN §6. Triggered by the session layer when an unanswered open concern surfaces during Debrief.

Open-concerns events are advisory only: Unity decides whether to surface a popup, which concern to sample, and whether the popup is dismissed, timed out, or accepted.

- **Disturbance gate (Unity-side):** before opening a popup, `GameEventDispatcher` rolls `Random.value < disturbanceProbability` (0.15 / 0.30 / 0.60). If the roll fails, the concern is silently skipped — the NPC does not react.
- If the roll passes: a random idle NPC plays `HandRaise`, a Q-mark spawns above their head (Phase 3.3.3 wiring).
- A floating panel appears in front of the player: **"May I ask a question? [Yes] [No]"** — auto-dismisses after **10 seconds** if ignored.
- On No / dismiss: NPC returns to Idle, Q-mark removed.
- On Yes: panel shows the question text + Close button.
- On Close: NPC returns to Idle, Q-mark removed. Another question may surface later.
- Only one popup is active at a time; the next can fire only after the current closes.
- Gated by the Live Q&A toggle from the Setup Panel (5.2).

### 5.8: Final Q&A

Per GAME_DESIGN §7.2. Triggered after **End Session: Yes** when the Final Q&A toggle is on.

- The session layer first runs its Clarification step. While it runs, a **"Preparing questions…"** spinner panel is shown.
- Once Clarification completes, the session layer reads `ClarificationResult.UnresolvedQuestions` and the Final Q&A panel appears showing one question at a time:
  - **Finish** — skips remaining questions and begins the Evaluation phase (5.11) immediately.
  - **Next** — the player's spoken answer to the current question is committed to the session module (the mic is already capturing; the session layer chunks and consumes audio internally). The next question is shown.

### 5.9: End Session — Controller Binding + Confirmation

`EndSessionController` (MonoBehaviour on `XRRig`, active during performance phase) monitors the **upper face button** (B on right / Y on left) on both controllers.

- On press: a floating confirmation panel appears in front of the player: **"End Session? [Yes] [No]"**.
- On No: the panel closes, session continues uninterrupted.
- On Yes: `SessionManager.EndSession()` is called → proceeds to Final Q&A (5.8) if enabled, otherwise directly to the Evaluation phase (5.11).
- The panel does not block locomotion or audio.

### 5.10: Echo Postprocessing

`EchoProcessor` lives on the mic AudioSource. Adds a Unity built-in `AudioEchoFilter` (delay ~15 ms, decay ratio driven by the Echo Level slider). Bypassed when the Echo toggle is off.

The filter affects only the local audio output (player's own headset). It is **not** in the path of the audio bytes sent to the session module.

### 5.11: Evaluation Progress Panel

After Final Q&A ends — or directly after **End Session: Yes** when Final Q&A is off — the in-scene UI is replaced by the **"Analyzing your presentation…"** panel.

- A progress bar with a percentage label updates as session-module stages complete. Stage weights (totaling 100 %): Summarizer MAP (30 %) → Summarizer REDUCE (20 %) → Ground Truth Doc processing (10 %, only when a doc is attached) → MainIdeaComparator (10 %) → DeepDive (30 %).
- **Ground Truth Doc processing** runs here, owned by the session module's `GroundTruthSummarizerRunner`: it consumes `GroundTruthDocInput`, reading text/Markdown by path and routing rasterized image inputs (`.jpg`/`.png` and PDF page 1) through the existing image-understanding path before GroundTruthSummarizer runs. Unity reflects the `GroundTruthSummarizerCompleted` event on the progress bar.
- A **"Cancel Analyzing / Return to Lobby"** button is always visible. Pressing it calls `GS_ConversationCancelProcess` on all active conversations, tears down the session layer, discards all in-memory state, unloads the current environment, and loads `Lobby.unity` additively while keeping `Shared` alive. No partial results are preserved.
- On DeepDive completion the panel transitions to the Results Screen (Phase 7.1).

### 5.12: System-Prompt Wiring

System-prompt content and schema design are owned by the session layer (`docs/SESSION_ARCHITECTURE.md`). The Lobby/UI does not call `GS_ConversationConfigCreate` directly. Instead:

1. Unity calls `SessionLayer.Begin(unitySessionConfig, gameSettings)`.
2. `SessionLayer` builds the module-side `GemmaStage.Session.SessionConfig`.
3. `SessionLayer` calls `Session.Start(moduleConfig)`.
4. The session module internally calls `GS_ConversationConfigCreate` when it constructs each native role conversation and its prompts/tools.

The module-side config carries language, duration/time limit, live/final Q&A toggles, and `GroundTruthDocInput`. The session module normalizes that Ground Truth input during the Evaluation phase (5.11).

Within this same wiring task, `SessionConfig.Language` must also be propagated into the role prompts that produce transcripts or user-facing text so the selected language changes runtime behavior rather than remaining a UI-only setting.

### 5.13: Tip Popups + Mic Availability Gate

- Add a shared tip-popup UI for the Lobby panels. Hover/focus on any Session Setup or Game Settings item shows a short explanation near that control.
- Reuse the same tip-popup system for disabled `Start Session` explanations.
- `Start Session` is disabled whenever no microphone is available from `Microphone.devices`; the popup explains that a microphone is required before a session can begin.

### 5.14: Universal Ground Truth Doc Input

- Add a single module-side Ground Truth contract, e.g. `GroundTruthDocInput`, with `None`, `FilePath`, and `RasterizedImageBytes` cases.
- `SessionLayer.Begin(...)` maps the Lobby attachment into that contract:
  - `.txt` / `.md` -> `FilePath`
  - `.jpg` / `.png` -> `RasterizedImageBytes`
  - `.pdf` -> `RasterizedImageBytes` from the attach-time first-page rasterization
- The session module owns the normalization step from `GroundTruthDocInput` into the text consumed by `GroundTruthSummarizer`.

---

## PHASE 6: AI Integration into Unity

*Connect GemmaStage.dll to the VR game. This is where the C# AI layer is built.*

### 6.1: P/Invoke Bridge

Copy the DLL and runtime dependencies into `Assets/Plugins/x86_64/`. Write a static C# class with P/Invoke declarations for every `GS_` export. Key considerations:

- All P/Invoke declarations use `CallingConvention.Cdecl`
- Marshal `bool` as a 1-byte unmanaged type
- Marshal `size_t` as a native-sized unsigned integer
- Returned strings are owned by native objects — copy immediately into managed memory

### 6.2: Inference Service — Unity-side wrapper around the session module

The actual P/Invoke surface is **synchronous and blocking** — every send call (text / audio / image) returns the completed response on the calling thread. Threading is the Unity wrapper's job, not the bridge's:

- A Unity scene component owns the session instance (start it via the session module's start helper, dispose on scene-component teardown). The session holds the engine handle and the live-phase role wrappers (audio, image, System1Reactor, System2Reflector, Inquirer); post-performance wrappers (Clarification, Summarizer, IdeaComprehension, GroundTruthSummarizer, MainIdeaComparator, DeepDive) are constructed on demand and disposed when those calls return.
- Audio chunks and board captures are pushed into the session module from a background worker — a single worker queue is sufficient since the engine slot semaphore already serializes inference.
- Post-performance lifecycle splits along the Final Q&A toggle. With Final Q&A **off** the Unity layer calls `GoFinalQAIfEnabledAndFinalize` once on End Session — that bundles `EndLivePhase` + `RunPostPerformancePipeline` (Summarizer → IdeaComprehension → GroundTruthSummarizer → MainIdeaComparator → DeepDive) into one synchronous return. With Final Q&A **on** the lifecycle controller drives the explicit sequence — `EndLivePhase` → `RunClarification` → `BeginFinalQARound` / `EndFinalQARound` per unresolved revised question → `RunPostPerformancePipeline` — because the Q&A loop is interactive. Each stage raises its matching typed session event as it completes.
- Cancellation: a session-cancel call cancels active native conversations via `GS_ConversationCancelProcess` and disposes the session. Unity uses this when the post-performance wait is dismissed with **Dismiss Session / Go to Lobby**.
- Subscribe to typed session events (see 6.4) and marshal them onto the Unity main thread for UI updates.

### 6.3: Per-role response parsers

Each session-layer role owns its own response parser inside the session module:

- audio role and image role — Perceptor split
- System1Reactor — topic + retelling per cycle (no thinking)
- System2Reflector — topic + cumulative `main_idea_understanding` plain-text paragraph per cycle (thinking on; ≤ 250-word cap; every distinct claim preserved, wording compressed)
- Inquirer — concern deltas (per-call `restricted` mode for Final Q&A)
- TranscriptSummarizer — split MAP (retelling parser + signals parser) and REDUCE outputs
- IdeaComprehension — single-field thesis output that finalizes the audience-side recall (`report_idea_thesis`)
- GroundTruthSummarizer — MAP per chunk (`report_chunk_claims` — flat new claims) and REDUCE (`{ main_thesis, claims[] }`) from the optional ground-truth file
- MainIdeaComparator — `report_thesis_comparison` (one call) plus `report_claim_coverage` (one call per anchor claim, judging the audience `main_idea_understanding` paragraph)
- Clarification — Revise (questions[] with type) and Resolve (resolved_ids per chunk)
- DeepDive — seven per-criterion sub-role parsers (`report_main_idea_clarity` / `report_structure` / `report_consistency_focus` / `report_support_justification` / `report_language_quality` / `report_emotional_delivery` / `report_qa_handling`); each returns one `{ value, verdict }` object that the coordinator composes into the final rubric record. Main Idea Clarity is the only sub-role with WithAnchor / NoAnchor system prompts (parser shared); the other six have a single prompt

Each parser deserializes the standard OpenAI `tool_calls` envelope into a role-specific typed record. They handle the same edge cases (text-only response, multiple tool_calls, malformed JSON) per role. **Unity game code never invokes these directly** — it consumes the typed session events (see 6.4); raw tool-call JSON does not cross the session-module boundary.

### 6.4: Game Event Dispatcher

Routes typed session events to game systems. The current event surface is:

- perceptor turn completed — per audio/image turn; carries the parsed Asr / I2t output
- cycle starting / cycle completed — input-pause boundaries plus the resulting IdeaReflector retelling/topic/main idea + Inquirer concern deltas
- open concerns updated — fires after every cycle (live + final) with the current open concern set, including the empty case; this is what Live Q&A subscribes to
- clarification completed (`ClarificationCompleted`) — fired after Clarification completes; `ClarificationResult.UnresolvedQuestions` is the ordered list that triggers the Final Q&A panel
- transcript summarizer / ground truth summarizer / main idea comparator / clarification / deepdive completed — post-performance stages
- deepdive sub-role completed — fires once per criterion (seven total) as each DeepDive sub-role parses or is programmatically skipped, ahead of the terminal deepdive completed event; carries the criterion identifier and the parsed `{ value, verdict }`
- session ended — fired at end of live phase, before any Final Q&A round

The Live Q&A trigger comes from the open-concerns event: when the toggle is on and the set is non-empty, the dispatcher picks a concern (`LiveQAConcernPicker.PickRandom` by default), triggers a HandRaise reaction on a random audience NPC, enables the Q-mark above the NPC, and opens the floating *"Can I ask a question?"* panel with the concern text (one popup at a time per GAME_DESIGN §6.1). On Yes the panel calls `BeginOnLiveQARound(concern)`, passing the picked `InquirerConcern` so the round's originating concern id flows through to the post-performance Q&A Handling sub-role's resolution decision; on the closing button it calls `EndOnLiveQARound()`. On the deepdive-completed event the dispatcher hands the result to the Results screen (Phase 7.1).

> The audience HandRaise signal originates from a cognitive cycle (an unanswered open concern from the live concern store). Tool calls are the session module's internal structured-output channel and are parsed inside the session module (§6.3) — Unity-facing events are typed objects and never carry raw tool-call JSON.

### 6.5: Audio Pipeline

Mic capture in VR → audio buffer → send to DLL via `GS_ConversationSendAudio`. **Always-on listening per GAME_DESIGN §4.2** — no push-to-talk. Audio is chunked during performance via the volume-based chunker built in Phase 2.2 (silence-gap-driven chunk emission), so the session module receives complete utterances rather than fixed-time slices.

### 6.6: Board Capture Pipeline

The Phase 4.3 board-capture event (board kind + PNG bytes) is subscribed by the inference service's image-send path → `GS_ConversationSendImage`. Capture itself runs inside the board logic: drawing board encodes its CPU texture directly to PNG; presentation board does a GPU readback before encoding. Triggered by the floating "Pay Attention" buttons (one per board — see 4.3).

### 6.7: Session Layer

Conversation orchestration for the live session (lifecycle, prompts, schemas, role coordination) is a separate module — see `docs/SESSION_ARCHITECTURE.md`. At this phase, the Unity layer instantiates a session from the session module and lets it drive conversation creation / destruction through the DLL.

---

## PHASE 7: Results & Polish

### 7.1: Results Screen

After the session ends, display a comprehensive evaluation in VR:
- Overall scores (clarity, structure, completeness)
- Specific issues flagged during the session
- Questions the model asked (and how the user handled them)
- Summary of the presentation's strengths and weaknesses
- Improvement suggestions

### 7.2: PDF Export

Generate a PDF report of the session results that the user can save. Include: scores, feedback, flagged issues, questions asked, timestamps, session settings. Accessible via a "Save Report" button on the results screen.

### 7.3: Visual & Audio Polish

Lighting and atmosphere for both environments. Background music routed through the master volume slider. Ambient classroom / auditorium sounds. Spatial audio for audience reactions (murmuring, shuffling). Particle effects for stage setting. Performance profiling for the 90 fps VR target.

UI animation pass for all world-space panels: smooth appear / disappear transitions (scale + fade) and button-press visual feedback (scale-down + color flash). Applies to the Lobby panels (Session Setup, Game Settings, Presentation Picker), the Live Q&A panel, the Final Q&A panel, the End Session confirmation panel, the "Preparing questions…" spinner, the Evaluation progress panel, and the Results Screen.

### 7.4: Lighting Bake

Phase 3 envs were authored with realtime lighting only (geometry flagged for GI but no bake — placeholders weren't worth baking). Now that geometry has stabilized, bake lightmaps for both Stage and Classroom:
- Author a `LightingSettings` asset per env scene.
- Bake (Mixed mode; Subtractive or Shadowmask depending on the perf budget after 7.3 polish).
- Verify 90fps target unchanged with NPCs in scene.

### 7.5: Affordance Callouts (controller binding hints)

Wire XRI's Affordance Callout system (already shipped as samples under `Assets/Samples/.../UI/Affordance Callout/`) to show binding hints on each controller during performance — e.g., the ray-toggle button highlight when the player approaches a floating UI panel, marker grip hint when reaching for a marker.

### 7.6: Reference Scene Cleanup

Delete `Assets/Scenes/BasicScene.unity` and `Assets/Scenes/SampleScene.unity` (kept through Phase 3–6 as live editor reference for Tutorial Player, Blaster grab pattern, Spatial Panel Manipulator). The XRI Starter Assets *prefabs* under `Assets/Samples/` stay — they are reused by Affordance Callouts (7.5), the marker grab (Phase 4), and the Setup/Comfort panels (Phase 5).

---

## PHASE 8: Build & Ship

Bundle the Gemma GGUF model (+ mmproj) in StreamingAssets. First-run setup experience (model verification — confirms the bundled model loaded; no in-app download flow). Windows standalone build with VR support.

**Explicitly out of scope:**
- **No session save/load.** Performances are not persisted; the Results screen is the only artifact a user can keep (via the PDF export from Phase 7.2).
- **No manual backend override UI.** The DLL auto-selects via the ggml backend registry — CUDA if present, else Vulkan, else CPU. (The `GS_EngineSettingsCreate` override remains as a dev / benchmarking tool, not a player-facing setting.)

Game Settings (vignette, turning mode, microphone, language, master audio volume, watch hand) **are** persisted across runs — that lives in Phase 5.3, not here.

---

## Timeline Estimate

| Period | Phase | Focus |
|--------|-------|-------|
| Week 1 | Phase 1 | DLL: llama.cpp integration, text/audio/image/tool-call verification, single-conversation audio PoC + VR load benchmarking (CUDA + Vulkan) |
| Week 2 | Phase 2 | Session architecture module on top of the DLL + volume-based audio chunker + end-to-end PoC driven by TED `.wav` files |
| Week 3 | Phase 3 | Unity VR project, environments, audience NPCs |
| Week 4 | Phase 4 | VR boards, markers, capture system |
| Week 5 | Phase 5 | Session management: Lobby scene + Setup/Comfort panels, setup→performance transition, RayController, wrist timer, Live + Final Q&A |
| Week 6 | Phase 6 | AI integration: P/Invoke bridge, service, parser, dispatcher, audio/image pipelines |
| Week 7 | Phase 7 | Results screen, PDF export, polish |
| Week 8 | Phase 8 | Build, test, ship |

---

## Risks

| Risk | Mitigation |
|------|------------|
| llama.cpp CMake + prebuilt DLL packaging on Windows | Unknown territory for this project. Verify early in Phase 1.1. Fallback: build llama.cpp from source via its upstream CMake. |
| Gemma 4 multimodal via `libmtmd` (audio + vision) | Must verify audio mmproj and vision mmproj both work for Gemma 4 E4B through the DLL. Test in Phase 1.1 before building image/audio-dependent features. |
| Backend selection (CUDA vs Vulkan vs CPU) on user machines | Use ggml backend registry + dynamic loading. Ship both CUDA and Vulkan backend DLLs; let ggml pick. Expose Unity override as escape hatch. |
| GPU contention VR + inference | Model weights + KV cache + VR rendering. Benchmark on both CUDA and Vulkan in Phase 1.2 PoC. Budget (~7.5 GB total, live and post-performance — mmproj remains engine-resident across phases) fits 8GB+ cards with some margin. |
| Inference starves VR frame rate | GPU interleaves compute and graphics queues. Benchmark in Phase 1.2 PoC. May need to throttle inference rate or switch backends under load. |
| Function call output quality from Gemma 4 E4B | Prefer the model chat template and llama.cpp built-ins first. System-prompt engineering still matters — test in Phase 1.1. |
| P/Invoke bridge + IL2CPP | Keep the Phase 2 bridge blocking-only and verify the same request/response path in Phase 6 before adding callback-based UX. |
| VR drawing board input accuracy | Asymmetric proximity band (front/back tolerance in surface-local Z) is the tuning knob — tight on the approach side prevents premature drawing, loose on the penetration side keeps drawing active when the marker presses through. Start simple (flat surface, large board). |
| PDF rendering in Unity | Use a library or render pages as pre-converted images at load time. |
| Context window exhaustion in long sessions | With llama.cpp we can set `n_ctx` freely; the session module pins it via the engine context-window setting (default 8K tokens) and the engine slot semaphore keeps one KV resident. Mitigation if needed: raise the context window, or rely on the post-performance Summarizer map-reduce. |
| Session layer design vs. DLL capability | Conversation count, lifecycle, and prompt design are owned by the session module (`docs/SESSION_ARCHITECTURE.md`) — not the DLL. DLL stays generic. |
| Audience NPC animation quality | Keep the animation set small and polished rather than large and rough. Mixamo provides good base animations. |
| Real-time feedback latency | llama.cpp first-token latency on CUDA is strong. Benchmark in Phase 1.2 PoC. |
| VR comfort (motion sickness) | Player is mostly stationary (standing at podium/front of class). Minimal locomotion needed. |
| GGUF model availability and quality | Multiple Gemma 4 E4B GGUF sources exist on HuggingFace. Pick a reputable quantizer, verify quality vs. reference. |

---

## Key Resources

| Resource | URL |
|----------|-----|
| llama.cpp (upstream) | https://github.com/ggml-org/llama.cpp |
| llama.cpp C API header | https://github.com/ggml-org/llama.cpp/blob/master/include/llama.h |
| llama.cpp multimodal (`libmtmd`) | https://github.com/ggml-org/llama.cpp/tree/master/tools/mtmd |
| llama.cpp build docs | https://github.com/ggml-org/llama.cpp/blob/master/docs/build.md |
| llama.cpp grammars (GBNF) | https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md |
| llama.cpp function calling | https://github.com/ggml-org/llama.cpp/blob/master/docs/function-calling.md |
| Gemma 4 GGUF (community) | https://huggingface.co/models?other=gemma&library=gguf |
| Mixamo (free characters + anims) | https://mixamo.com |
| OpenXR Unity docs | https://docs.unity3d.com/Manual/com.unity.xr.openxr.html |
| XR Interaction Toolkit | https://docs.unity3d.com/Packages/com.unity.xr.interaction.toolkit@latest |

---

## VRAM Budget

Every conversation allocates a per-call `llama_context` and disposes it after parsing — no long-lived KV caches. Every role uses the same engine context window (default 8K tokens), and an engine slot semaphore enforces single-KV residency. See `docs/SESSION_ARCHITECTURE.md` §14.

| Component | VRAM | Notes |
|-----------|------|-------|
| Gemma 4 E4B GGUF (Q4_K_M) | ~5.0 GB on disk | On-device LLM weights — shared across all conversations; runtime VRAM trace still needs validation |
| KV cache (8K ctx) | ~0.25–0.4 GB | One context at a time across all roles (Asr / I2T / System1Reactor / System2Reflector / Inquirer / Clarification / TranscriptSummarizer / IdeaComprehension / GroundTruthSummarizer / MainIdeaComparator / DeepDive) |
| mmproj (vision / audio encoder) | ~0.3–0.5 GB | Loaded with the engine when an mmproj path is configured; resident until engine deletion |
| VR rendering | ~1–2 GB | URP, depends on scene complexity |
| **Total (live + post-performance)** | **~7.5 GB typical** | One KV at a time across every role; mmproj stays engine-resident through both phases. Fits 12GB+ cards comfortably, workable on 8GB cards |

No TTS VRAM overhead. Conversations alternate sequentially — only one KV is resident at any moment. If the 8GB budget is too tight, options include: smaller quant (Q3_K_M), CPU offload for some layers, or dropping the engine context window below 8K.
