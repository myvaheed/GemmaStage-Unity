# GemmaStage

GemmaStage is a VR practice environment for **explaining ideas under pressure**. You stand on a virtual stage (or in a classroom), explain an idea out loud to a simulated audience, get interrupted with questions, and receive structured feedback on how clearly the idea came across — all evaluated locally by an on-device Gemma 4 model running through `llama.cpp`.

> The system does not judge whether the idea is correct. It asks whether the idea was communicated clearly enough to be understood.

---

## What it does

- **Real-time perception.** The player's voice is transcribed chunk-by-chunk; whiteboard / slide images can be captured on demand with the *Pay Attention* button.
- **Audience simulation.** An on-device cognitive loop maintains a running "what the listener has understood so far" plus an evolving list of open concerns. When a concern is open, an NPC raises a hand and the game offers a Live Q&A popup.
- **Final Q&A.** After the player ends the session, the model surfaces unresolved concerns as follow-up questions.
- **Evaluation.** A post-performance pipeline (transcript summarization → optional ground-truth comparison → seven-criterion DeepDive) produces a rubric-based feedback report.
- **Fully local.** No network calls. Audio, image, and text inference all run on the user's GPU/CPU via `llama.cpp`.

See [docs/GAME_DESIGN.md](docs/GAME_DESIGN.md) for the player-facing experience and [docs/SESSION_ARCHITECTURE.md](docs/SESSION_ARCHITECTURE.md) for the session/inference architecture.

---

## System requirements

| Component | Minimum |
|-----------|---------|
| OS        | Windows 10 / 11 (x64) |
| VRAM      | **14 GB** (NVIDIA CUDA preferred; Vulkan supported; CPU fallback available but not recommended) |
| RAM       | **8 GB** |
| VR        | OpenXR-compatible PC VR headset (SteamVR-tested) |

Storage budget for the models is roughly **6 GB**.

---

## Models

GemmaStage uses Gemma 4 E4B (instruction-tuned) plus its multimodal projector. Both files must be placed in a `models/` folder at the **repository root**:

```
<repo-root>/
└── models/
    ├── gemma-4-E4B-it-Q4_K_M.gguf
    └── mmproj-BF16.gguf
```

Download from Hugging Face (unsloth mirror):

- [`gemma-4-E4B-it-Q4_K_M.gguf`](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/blob/main/gemma-4-E4B-it-Q4_K_M.gguf) — main weights (~5 GB)
- [`mmproj-BF16.gguf`](https://huggingface.co/unsloth/gemma-4-E4B-it-GGUF/blob/main/mmproj-BF16.gguf) — vision / audio encoder (required for board capture and microphone input)

The `models/` folder is gitignored. The Unity build picks the files up from `StreamingAssets`; for editor and console runs the loader reads them directly from `models/`.

---

## Repository layout

| Path | Purpose |
|------|---------|
| [GemmaStage/](GemmaStage/) | Thin C++ DLL (`GemmaStage.dll`) wrapping the `llama.cpp` C API |
| [GemmaStage.Session/](GemmaStage.Session/) | C# session module — Perceptor, CognitiveCoordinator, post-performance pipeline |
| [GemmaStage.Session.PoC/](GemmaStage.Session.PoC/) | Console PoC driving the session module against a `.wav` file |
| [GemmaStage.Session.Tests/](GemmaStage.Session.Tests/) | Unit / integration tests for the session module |
| [GS.Unity/](GS.Unity/) | Unity 6 LTS VR project (OpenXR, Stage + Classroom scenes) |
| [llama_prebuilt/](llama_prebuilt/) | Prebuilt `llama.cpp` + ggml runtime DLLs (CUDA + Vulkan + CPU) |
| [docs/](docs/) | Concept, game design, session architecture, evaluation rubric |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | Phase roadmap |
| [CLAUDE.md](CLAUDE.md) | Project-wide engineering notes |
| `models/` | Local model cache (gitignored — see above) |

---

## Building

### Native DLL (`GemmaStage.dll`)

From the repo root in PowerShell:

```powershell
cmake -S GemmaStage -B GemmaStage/build -DCMAKE_BUILD_TYPE=Release
cmake --build GemmaStage/build --config Release
```

Output: `GemmaStage/build/Release/GemmaStage.dll` plus the `llama.cpp` / ggml runtime DLLs copied alongside.

Smoke test (loads the model, no inference):

```powershell
./GemmaStage/build/Release/test_load.exe `
  --model_path=./models/gemma-4-E4B-it-Q4_K_M.gguf `
  --mmproj_path=./models/gemma-4-E4B-it-BF16.gguf
```

### Session module + Unity

- Open [GS.Unity/](GS.Unity/) in **Unity 6 LTS**. The session C# module is referenced as a local package.
- The native plugin is consumed from `Assets/Plugins/x86_64/` — the build step above copies the required DLLs there.
- For headless experimentation use [GemmaStage.Session.PoC](GemmaStage.Session.PoC/), which drives the full live + post-performance pipeline against a `.wav` file.

---

## Architecture in one diagram

```
Raw inputs (audio chunks + on-demand images)
        │
        ▼
   Perceptor  (Asr per chunk, I2t per image — stateless calls)
        │
        ▼
   CognitiveCoordinator cycle (~once per minute)
        │   1. System1Reactor   — fast factual retelling
        │   2. System2Reflector — cumulative main-idea paragraph
        │   3. Inquirer         — open-concern delta
        │
        ▼
   (live phase loops back to Perceptor; OpenConcerns drives hand-raises)
        │
   End Session
        │
        ▼
   Final Q&A (optional)  →  Post-performance pipeline:
        TranscriptSummarizer → IdeaComprehension
        → (GroundTruthSummarizer → MainIdeaComparator if GT attached)
        → DeepDive (seven per-criterion sub-roles)
        │
        ▼
   Results screen + PDF export
```

The C++ DLL exposes a small `GS_*` surface (engine, conversation, send-text / -audio / -image, cancellation, response-string getter). The Unity layer never sees raw tool-call JSON — the session module parses it into typed `SessionEvents` that the game-event dispatcher binds to NPC reactions, popups, and the evaluation panel.

---

## Privacy

Everything — microphone audio, board captures, transcript, evaluation — stays on the local machine. There is no telemetry and no network dependency once the model files are on disk.

---

## License

See repository for license terms.
