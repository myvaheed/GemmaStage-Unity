# GemmaStage — Session Architecture

## 1. Purpose

GemmaStage is a local session-based system for analyzing presentations, explanations, teaching sessions, and Q&A.

The system does **not** evaluate whether an idea is objectively correct or valuable. It evaluates **how well the idea is communicated**.

Its core question is:

> Was the speaker understood the way they intended to be understood?

The architecture is built around the following model roles:

1. **Perceptor** — perceives the talk in real time (audio transcription + image examination)
2. **IdeaReflector** — split per cycle into two sub-roles: **System1Reactor** (no-thinking, fast factual retelling) and **System2Reflector** (thinking-on, integrates the latest retelling into a cumulative plain-text `main_idea_understanding` capped at 250 words — every distinct claim preserved, wording compressed)
3. **Inquirer** — manages the live concern set (open questions an attentive listener would still want answered)
4. **TranscriptSummarizer** — post-performance map-reduce over the transcript
5. **GroundTruthSummarizer** — extracts a flat list of factual claims plus a thesis from an optional ground-truth file supplied before the session
6. **IdeaComprehension** — single post-performance call that distills the cumulative `main_idea_understanding` into one short thesis (per-cycle reflections do not emit a thesis)
7. **MainIdeaComparator** — anchor-driven coverage: one thesis-comparison call plus N per-claim coverage calls (each judges whether the audience's `main_idea_understanding` paragraph supports one anchor claim); runs only when ground truth is present
8. **Clarification** — filters and merges archived concerns at the start of Final Q&A
9. **DeepDive** — coordinator over seven per-criterion sub-roles; the Main Idea Clarity sub-role uses a with-anchor prompt when GT ran and a no-anchor prompt otherwise

---

## 2. Core Principle

The system intentionally does not preserve a single objective truth during the live session. It maintains an **audience-side recall** — a plain-text `main_idea_understanding` that an ideal attentive listener would have built up after hearing the talk so far — and tracks **open concerns** (questions that listener would still want answered).

After the session ends, the system independently extracts what was actually said (the transcript-side main idea) and, if the speaker attached a ground-truth file before the session, what the speaker intended to say. The post-performance comparator runs only when GT is present: one thesis-comparison call (anchor thesis vs audience thesis) plus one coverage call per anchor claim that judges whether the audience's `main_idea_understanding` paragraph supports that claim (`yes` / `partial` / `no`). DeepDive produces the final evaluation against a fixed rubric as a coordinator over seven per-criterion sub-roles; only the Main Idea Clarity sub-role splits into with-anchor and no-anchor variants depending on whether GT was attached.

Communication quality is not the same as transcript quality. A person may say many words and still fail to communicate the intended idea.

---

## 2.1 Research-grounded Design Pillars

Two architecture choices diverge from a "transcript-fidelity" baseline and deserve explicit grounding because they shape the meaning of every downstream score.

### 2.1.1 Audience model — ideal attentive listener

The session does not simulate a *realistic* listener (one who drifts, fatigues, suffers serial-position effects, or fails to encode mid-talk content). It simulates an **ideal attentive listener**: a listener who hears every word the speaker says, has unbroken attention, but still must compress what they hear into a working understanding rather than a transcript.

The reason is the system's purpose. This is a feedback tool for the speaker, not a forecast of how a real audience will perform. If the audience model fails to recall a point, that failure must be attributable to *what the speaker said* and how clearly they said it, not to *modelled inattention drift unrelated to delivery*. Mixing in attention drift would conflate speaker performance with audience performance and make the rubric scores noisy as feedback.

What this listener still must do, even when ideal:

- **Compress in wording, not in content.** Cross-paradigm comparison of human summary datasets puts whole-talk human summaries in the **3–8 % of source** band. A listener carries away every claim, fact, and support pillar — but in compressed phrasing, not verbatim sentences.
- **Build a running situation model.** Comprehension research (situation-model and fuzzy-trace traditions) shows listeners integrate new content into a cumulative mental model rather than storing sentences verbatim.
- **Preserve all distinct claims.** Every main claim, fact, and support pillar is retained across the talk; only filler, near-duplicates, and least-informative qualifiers are dropped to fit the budget.

Operational consequence: System2's `main_idea_understanding` is a **plain-text paragraph capped at 250 words**, evolved cumulatively by adding new claims from each cycle's retelling. As the paragraph approaches the cap, the model compresses wording (combining related claims into compound sentences, stripping filler, using tighter syntax) rather than dropping content. The audience-side **thesis** (≤ 50 words) — produced post-performance by IdeaComprehension — is the only field where the gist rule applies. See §4.2 and §13.6 for the runtime contract.

### 2.1.2 Per-cycle split — System1 reactor + System2 reflector

Each cognitive cycle runs two sub-roles instead of one monolithic IdeaReflector:

- **System1Reactor** — thinking disabled. Inputs: current `main_idea_understanding`, last 4 retellings, raw transcript window. Output: `topic` + a short factual `retelling` of what the speaker just said this cycle.
- **System2Reflector** — thinking enabled. Inputs: prior topic, prior `main_idea_understanding`, the just-emitted retelling. Output: refreshed `topic` + the cumulative `main_idea_understanding` (≤ 250 words; new claims added, existing claims preserved, wording compressed as the paragraph approaches the cap).

The split is grounded in dual-process accounts of cognition (the "fast / slow" or System 1 / System 2 framing). The motivation is both cognitive and mechanical:

- **Cognitive fit.** A listener does two things per stretch of talk — first recognise *what was just said* (factual capture), then integrate it into *what the talk is about overall*. The split mirrors that: System1 is per-cycle factual capture; System2 is integration into the running understanding.
- **Mechanical reliability.** A 4 B local model under constrained decoding is more reliable on small, single-purpose schemas than on one fat schema that bundles transcription + integration + long structured output. Splitting reduces per-call prefill, lets System1 skip thinking entirely (cheap), and confines the expensive thinking-on call to the smaller integrative task.

Operational consequence: every cycle does exactly two model calls in this order. Topic carries through both — System1 emits an updated topic from the raw content, System2 may refine it from the integrated paragraph; the runtime applies the more recent value (System2 wins when both differ from the prior topic).

---

## 3. High-Level Model

```text
Raw inputs (audio / images)
         │
         ▼
┌──────────────────────────────────────────────┐
│ Perceptor (per-call one-shot, fresh ctx)     │
│   Asr conversation  — per audio chunk        │
│   I2t conversation  — per image              │
└──────────────────────────────────────────────┘
         │   (audio + image examinations interleave
         │    into a per-cycle raw content buffer)
         ▼
┌──────────────────────────────────────────────┐
│ CognitiveCoordinator cycle                   │
│   1. IdeaReflector cycle:                    │
│        System1Reactor   (topic + retelling)  │
│        System2Reflector (main_idea_           │
│                          understanding)      │
│   2. Inquirer  (concerns)                    │
└──────────────────────────────────────────────┘
         │
         │   wrapper module emits OpenConcerns event
         │   ↓ (live phase: loops back to Perceptor)
         │
         ▼
   User presses End Session
         │
         │   in-flight audio is chunked, one final
         │   cognitive cycle absorbs it
         ▼
┌──────────────────────────────────────────────┐
│ Bundled post-performance pipeline            │
│   Final Q&A (if enabled):                    │
│     Clarification → Next/Finish loop         │
│     (restricted cognitive cycles)            │
│   TranscriptSummarizer  (map-reduce)         │
│   GroundTruthSummarizer  (only if GT file)   │
│   MainIdeaComparator                         │
│   DeepDive  (final evaluation)               │
└──────────────────────────────────────────────┘
```

---

## 4. Roles

### 4.1 Perceptor

Two per-call one-shot single-modality conversations sharing the same engine. Each input gets a fresh `llama_context`; the model produces one structured response; the context is torn down after parsing. No history is carried across calls.

- **Asr conversation** — per audio chunk. Tool: `report_audio_observation`. Output schema: §7.1.
- **I2t conversation** — per image. Tool: `report_image_observation`. Output schema: §7.2.

Perceptor does not hold session state. `topic`, the audience-side recall (`AudienceSideRecall`), and the live concern set live in runtime stores.

### 4.2 IdeaReflector — System1Reactor + System2Reflector

Each cognitive cycle invokes two sub-roles in order. The split (rationale in §2.1.2) replaces what used to be a single combined IdeaReflector role.

#### 4.2.1 System1Reactor (per cycle, no thinking)

Inputs:

- the runtime-held `topic`
- the current `main_idea_understanding` (plain-text paragraph; empty until first System2 succeeds)
- the **last 4 retellings** from RetellingsHistory (oldest first), as text
- the **raw content for this cycle** (see §6)

Output:

```json
{
  "topic": "current best short label for the talk (1–4 words) — carry the prior topic forward verbatim when nothing in this cycle revises it; \"unknown\" only when no topic has emerged",
  "retelling": "short factual paragraph of what the speaker said in THIS cycle's raw content — claims, facts, support pillars, no narrative framing"
}
```

System1 is intentionally cheap: thinking is disabled, the schema is two short strings, and the role does not integrate. The runtime appends each retelling to RetellingsHistory after parsing, and applies `topic` to the live state-tracker (sentinel `"unknown"` leaves the prior topic untouched).

#### 4.2.2 System2Reflector (per cycle, thinking on)

Inputs:

- the runtime-held `topic` (after System1 may have updated it)
- the prior `main_idea_understanding` (the running cumulative paragraph **before** this cycle's enrichment)
- the latest retelling (just produced by System1 — this cycle only)

Output:

```json
{
  "topic": "current best short label, refined from the integrated paragraph",
  "main_idea_understanding": "cumulative plain-text paragraph (≤ 250 words) — every claim, fact, and support pillar preserved; wording compressed to fit the cap"
}
```

Hard rules driven by the prompt and grounded in §2.1.1:

- **HARD CAP: never exceed 250 words.** The cap wins.
- Preserve every distinct claim, fact, and support pillar previously captured. Add new claims, facts, and support pillars from this cycle's retelling.
- To fit the cap, compress the *wording*: combine related claims into compound sentences (using "and", commas, semicolons), strip filler phrases, use shorter synonyms, subordinate less central facts as clauses.
- Merge near-duplicates: a restated point integrates as nuance, not a new sentence.
- If after compression the paragraph still exceeds the cap, drop only the least-informative example, qualifier, or adjective — never a distinct claim.
- Never invent facts. When the latest retelling adds nothing genuinely new, return the prior paragraph unchanged.

The runtime applies `topic` (System2's value wins over System1's when both updated; sentinel `"unknown"` is ignored) and replaces the live `main_idea_understanding` with the new paragraph. There is **no domain-keyed structure** — the audience side is plain text end to end.

The audience-side **thesis** is **not** emitted per cycle. It is inferred once at end-of-live-phase by a one-shot **IdeaComprehension** call (see §12.3) that reads the final `main_idea_understanding` and produces the single load-bearing thesis the listener walked away with. Until that call runs, the rendered audience-side recall reads `Thesis: (not yet established)`.

#### 4.2.3 Failure handling

If System1 fails its retry budget the cycle has no retelling and System2 is skipped; the live state is unchanged for that cycle and the runtime warns. If System2 fails after System1 succeeded, the retelling is still appended to RetellingsHistory but `main_idea_understanding` is unchanged; the runtime warns and the next cycle will re-attempt integration.

### 4.3 Inquirer

Inquirer manages the live concern set only. It does not revise the main idea — that is IdeaReflector's job.

Per call it sees:

- **all retellings** produced so far this session
- the **rendered audience-side recall** (topic + thesis line + the cumulative `main_idea_understanding` paragraph, as a flat string)
- the **current open concerns**

The full retellings history is what lets Inquirer judge which open concerns are now resolved or irrelevant and what new questions an attentive listener would still want answered.

Output:

```json
{
  "new_concerns": [
    { "id": 0, "question": "what currently remains concerning or unclear",
      "type": "topic_unknown | comprehension_gap | detail_request" }
  ],
  "removed_concerns": [
    { "id": 0, "cause": "resolved | irrelevant", "note": "short explanation" }
  ]
}
```

Constraints:

- at most **3** new concerns per cycle
- the live deque is capped at **10** total open concerns; overflow is moved to the concern archive
- runtime reserves ids; the model must not reuse or invent old ids
- runtime, not the model, applies all list mutations

See §8 for concern type semantics and the runtime-derived `confusion_score`.

### 4.4 TranscriptSummarizer

TranscriptSummarizer runs after the live phase. It walks the transcript through a map-reduce pipeline. The MAP stage emits per-chunk retellings plus structure / consistency / support enum tags; those per-chunk artifacts are consumed directly by the per-criterion DeepDive sub-roles. The REDUCE stage synthesizes a single inferred main idea from the chunk retellings — every claim and support pillar preserved, wording compressed to fit the 250-word cap. The model never sees the audience-side recall. See §12.1.

### 4.5 GroundTruthSummarizer

GroundTruthSummarizer runs only if a ground-truth document is attached during session setup. At the session-module boundary, that document is represented as a small universal contract, e.g. `GroundTruthDocInput`, with three cases:

- `None`
- `FilePath`
- `RasterizedImageBytes`

The role consumes plain text, so the session module performs one normalization step before prompting GroundTruthSummarizer.

Normalization rules:

- **`.txt` / `.md`** — arrive as `FilePath`; the module reads them directly as UTF-8 text.
- **`.jpg` / `.png`** — arrive as `RasterizedImageBytes`; the module routes them through the existing `I2t` conversation and uses its `examination` output.
- **`.pdf`** — the Unity Lobby rasterizes page 1 at attach time; `SessionLayer` forwards that page as `RasterizedImageBytes`. Multi-page PDFs still use page 1 only; remaining pages are dropped.

After normalization, the role runs a **map-reduce** that produces a flat list of factual claims plus a thesis — there is no domain-keyed structure on the GT side. The runtime sentence-splits the text deterministically (regex on `.!?`) and feeds it through the shared `TranscriptChunkBuilder` at `SessionConfig.TranscriptSummarizerTokenBudget` (default 500 tokens, sized to fit Gemma's 512-token sliding window).

- **MAP** — one call per chunk. Tool: `report_chunk_claims`. Output: `{ claims: [string] }` — append-only new factual claims this chunk adds. Each chunk's prompt embeds the **prior accumulated claim list** so the model can avoid restating already-captured claims; chunks emit only what is genuinely new. The runtime appends each emitted claim to a flat in-memory accumulator after parsing.
- **REDUCE** — one call. Tool: `report_ground_truth_decomposition`. Input: the accumulated claim list as bullets. Output: `{ main_thesis (≤ 50 words; overarching message — gist rule applies), claims (plain text, one claim per line, total ≤ 250 words — all distinct claims preserved, wording compressed) }`. The runtime splits `claims` on newlines into `IReadOnlyList<string>`.

The structured output `{ MainThesis, Claims }` is fed directly to `MainIdeaComparator` as the anchor decomposition — no second decompose call is needed.

If no GT doc is attached, normalization fails, the normalized text is empty, or REDUCE fails, this stage is skipped and `MainIdeaComparator` does not run; DeepDive falls back to its no-anchor prompt variant.

### 4.6 MainIdeaComparator

MainIdeaComparator runs **only when ground truth is present**. Both sides are already decomposed when it starts: the audience side as `AudienceSideRecall` (`Topic`, `Thesis`, `MainIdeaUnderstanding` plain-text paragraph — built by System1+System2 across the live phase, thesis finalized by IdeaComprehension), and the anchor side as `{ MainThesis, Claims }` produced by GroundTruthSummarizer.

Anchor-driven coverage flow:

1. **Thesis comparison** — one call. Tool: `report_thesis_comparison`. Inputs: anchor thesis + audience thesis. Output: `{ thesis_comparison }` — a short prose paragraph (≤ 50 words) describing what the listener got right, what they missed or weakened, and what they emphasised differently. Plain language, no bullets. A failed thesis call is logged with a warn and a fixed placeholder string is recorded; the comparator does not abort.
2. **Per-claim coverage** — one entailment call per anchor claim (N calls; sequential under the single-KV residency model). Tool: `report_claim_coverage`. Inputs: one anchor claim + the audience's full `main_idea_understanding` paragraph. Output: `coverage ∈ { yes, partial, no }` plus optional `evidence` (≤ 30 words paraphrase from the paragraph). A failed coverage call is treated as `no` plus a warn — one bad call does not abort the comparator.
3. **Recall (programmatic)** — `Recall = (Σ coverage scores) / N` with `yes = 1.0`, `partial = 0.5`, `no = 0.0`. `N = |anchor.claims|`; an empty claim list yields `Recall = 0.0` (no NaN).

The comparator no longer performs canonical-key alignment, has no concept of "missed" or "off-topic" topics, and does not run a separate drift call — the audience side is plain text, not a domain-keyed structure, so set-diff over canonical keys would be meaningless. Drift signal is captured qualitatively by `thesis_comparison`, and per-claim coverage measures whether the speaker actually transferred each anchor claim.

When GT is absent the comparator does not run at all. DeepDive then takes its no-anchor prompt variant (see §4.7).

See §12.4.

### 4.7 DeepDive

DeepDive runs as a coordinator over **seven per-criterion sub-roles**, each a brand-new conversation with its own system prompt, tool schema, parser, and targeted input. Sub-roles run sequentially under the engine's single-KV slot; a per-sub-role event fires as each completes and the seven criterion outputs are composed back into one rubric-based evaluation (§12.5).

Sub-role inputs are tightly scoped per criterion:

1. **Main Idea Clarity** — audience-side recall (`main_idea_understanding`) + confusion dynamics + (when GT ran) the comparator output and a deterministic suggested score derived from Recall. WithAnchor judges on the suggested score with thesis-gate adjustments (divergent thesis caps at 2, partial caps at 3) plus confusion dynamics; NoAnchor judges on the qualitative match between the inferred main idea and the audience-side recall plus confusion dynamics. **The runtime does not override** the model's value here — thesis-divergence is a qualitative judgment.
2. **Structure** — chunk-count-forked. With **≥ 3 chunks**: chunk retellings paired with their per-chunk structure labels (`intro`/`development`/`conclusion`/`unclear`) + confusion dynamics + a deterministic computed score; the model writes a verdict only, the runtime overrides the score with the computed value. With **1–2 chunks**: a short-transcript variant feeds the raw transcript + chunk retellings + confusion dynamics, and the model scores 1–5 directly against a level rubric in the system prompt (no override). With **0 chunks**: skipped programmatically (N/A, no model call).
3. **Consistency & Focus** — chunk-count-forked. With **≥ 3 chunks**: chunk retellings paired with consistency labels (`consistent`/`minor_drift`/`major_drift`) + confusion dynamics + a deterministic computed score; runtime overrides the score with the computed value. With **1–2 chunks**: short-transcript variant (raw transcript + chunk retellings + confusion dynamics) scored 1–5 by the model against a level rubric, no override. With **0 chunks**: N/A.
4. **Support & Justification** — chunk-count-forked. With **≥ 3 chunks**: chunk retellings paired with support labels (`none`/`weak`/`moderate`/`strong`) + a deterministic computed score (no confusion dynamics on this branch); runtime overrides the score with the computed value. With **1–2 chunks**: short-transcript variant (raw transcript + chunk retellings + confusion dynamics) scored 1–5 by the model against a level rubric, no override. With **0 chunks**: N/A.
5. **Language Quality** — raw transcript slices for chunks tagged `grammar=poor` or `grammar=moderate`, plus the deterministic `computed_language_quality`. The model writes a verdict only; the runtime overrides the score with the computed value.
6. **Emotional Delivery** — the inferred main idea (so the model can judge appropriate tone for the topic) plus the emotion dynamics, whose notes admit `enthusiastic` alongside `tense` and `uncertain` (calm chunks excluded from notes but kept in the distribution). Always runs; the model commits to a 1–5 score even when the distribution is all calm — a calm delivery is rated against the topic's demands (calm fits a scientific topic; a dramatic / persuasive topic expects variance).
7. **Q&A Handling** — one entry per closed Q&A round with phase (`Live`/`Final`), question text, the answer transcript span, and resolution (`Resolved` if the originating concern appears in subsequent inquirer reflections with `cause: resolved` or in Clarification's resolved set, else `NotAnswered`). Skipped programmatically when no Q&A rounds occurred.

See §12.5 for the per-sub-role I/O detail and §13 for the per-sub-role input shapes.

### 4.8 Clarification

Clarification runs at the start of Final Q&A (only if Final Q&A is enabled). It walks the concern archive plus any concerns still open at end-of-session and produces the Final Q&A panel set in two stages, each with its own system prompt and tools schema:

1. **Revise** — one call. Takes the archived concerns (id, question, type) and emits a deduplicated, lightly normalized question list. When two concerns are merged the lower id is kept and the type is lifted to the more severe value (`topic_unknown` > `comprehension_gap` > `detail_request`).
2. **Resolve** — one call per transcript chunk over the revised list. Marks ids that the chunk plainly answers as resolved. Necessary for early-overflow concerns whose answer landed later in the talk: those concerns were archived before the live Inquirer could see the answer, so without this walk they would surface in the Final Q&A panel as if unanswered.

The unresolved revised questions feed the Final Q&A panel; the resolved ids contribute to the post-performance concerns-resolved count (§13.4).

---

## 5. Runtime — CognitiveCoordinator

The CognitiveCoordinator wires Perceptor inputs into cycles of `IdeaReflector → Inquirer`. It owns:

- input queue
- transcript store
- image store
- metrics store
- live concern store (mirror of Inquirer's deque)
- concern archive
- retellings history (full, unbounded — Inquirer reads it whole each cycle)
- Q&A rounds history (one closed entry per Live or Final round — phase, originating concern id, question text, opened/closed timestamps; consumed by the post-performance Q&A Handling sub-role)
- audience-side recall state (`topic` + cumulative `main_idea_understanding` plain-text paragraph; thesis populated post-performance by IdeaComprehension)
- **raw content buffer** for the current cycle (§6)
- cycle scheduler
- prompt builder per role

A wrapping session module sits on top of the CognitiveCoordinator and emits typed events (Perceptor turn completed, cycle started/completed, OpenConcerns, post-performance stage events). Game-side code consumes these typed events; raw tool-call JSON does not cross the boundary.

---

## 6. Raw Content Buffer

Each cognitive cycle consumes a **raw content buffer** that has accumulated since the previous cycle. The buffer is a chronological sequence of:

- **audio transcripts** — appended as Asr emits each chunk
- **image examinations** — appended in place when an image is captured (Pay Attention)

A typical buffer for a cycle that included one image looks like:

```
[Audio chunk 1][Audio chunk 2][Image examination][Audio chunk 3]
```

When an image arrives:

1. The chunker emits whatever audio is currently in flight as a chunk.
2. Asr transcribes that chunk and appends it to the buffer.
3. I2t examines the image and appends the examination to the buffer.
4. Audio capture resumes; the next audio chunk is appended whenever it completes.

Image arrival never triggers a cognitive cycle on its own — see §7.

---

## 7. Cycle Trigger

A cognitive cycle fires when **both** conditions are met:

1. **Elapsed-time gate (`D`)** — at least `D` seconds have passed since the previous cycle (or since session start). Default `D ≈ 60 s`.
2. **Boundary signal** — the most recently completed audio turn carries `chunk_completed = true`. The runtime derives `chunk_completed` post-hoc from the transcript's trailing punctuation (after stripping closing quotes / brackets / whitespace), overriding any model self-report — see §7.1.

With `D ≈ 60 s` and typical chunks of 4–10 s, cycles naturally land roughly once per minute.

While a cycle runs:

- live input is paused
- new input goes to a queue
- the runtime constructs IdeaReflector and Inquirer prompts from runtime stores
- each role is invoked sequentially in a fresh `llama_context`, disposed after parsing
- the main idea state is updated, the retelling is appended to the retellings history, concern mutations are applied
- the wrapper emits `OpenConcerns` (every cycle, including when the open set is empty — the game decides whether to surface a popup; see §9)
- the queue drains back through Perceptor

### 7.1 Asr output schema

```json
{
  "clarity": "messy | normal",
  "transcript_of_this_chunk": "transcript of the spoken chunk",
  "emotion": "calm | enthusiastic | tense | uncertain",
  "grammar": "poor | moderate | good | excellent",
  "chunk_completed": true
}
```

- `clarity` — TRANSCRIBING clarity of this audio chunk (acoustic quality), not speaker self-assurance. `normal` = usable; `messy` = too noisy / fragmented to trust as speech.
- `transcript_of_this_chunk` — transcript of the spoken chunk. When `clarity = messy`, this becomes a short bracketed explanation of the failure (e.g. `[heavy background noise; speech unintelligible]`).
- `emotion` / `grammar` — optional, dropped when `clarity = messy`.
- `chunk_completed` — whether the chunk ends at a meaningful sentence or clause boundary. **Runtime overrides post-hoc** based on the transcript's trailing punctuation; the model self-report is only a hint. Forced to `false` when `clarity = messy`.

### 7.2 I2t output schema

```json
{
  "examination": "detailed but token-budgeted description of what is shown in the image"
}
```

`examination` is capped at a small upper bound so a verbose image description cannot dominate the cycle's raw content.

---

## 8. Concerns Model

Each new concern is tagged with a `type`:

- **`topic_unknown`** — listener cannot name what the talk is about. The runtime auto-inserts a canonical "What is the topic?" concern with this type while topic is unknown.
- **`comprehension_gap`** — listener heard the speaker but cannot decode the meaning, terminology, or how it fits.
- **`detail_request`** — listener understood the point and wants more depth, an example, or justification.

After applying a reflection (and after overflow archiving), runtime derives `confusion_score`:

- ANY `topic_unknown` open → **`very_high`**
- ELSE `comprehension_gap` ≥ 3 → **`high`**
- ELSE `comprehension_gap` ≥ 1 → **`medium`**
- ELSE → **`low`**

`confusion_score` is exposed on the session state snapshot.

After each cycle, the wrapper emits an `OpenConcerns` event carrying the current open set. The event fires every cycle, including when the set is empty; the game layer decides whether to act on it (§9).

When the live phase ends, any concerns still open are moved into the concern archive — they remain available to Clarification and Final Q&A.

---

## 9. On-Live Q&A

On-Live Q&A is gated by the **Live Q&A** session toggle.

Flow:

1. Cognitive cycle finishes → wrapper emits `OpenConcerns(open_set)`.
2. If Live Q&A is **disabled**: the game ignores the event.
3. Otherwise the game shows a small floating panel: *"May I ask a question?"* with **Yes** / **No**.
   The session module does not decide whether to show, dismiss, time out, or accept this popup; Unity owns that UI flow and samples the question from the open set.
4. If user presses **No** or ignores the panel for **10 s**: panel closes; no Q&A this cycle.
5. If user presses **Yes**:
   - The game picks one concern from the open set (random for now; the algorithm can be refined later).
   - The runtime enters On-Live Q&A mode: cycle scheduling is suspended.
   - In-flight audio is chunked (Asr transcribes it) and appended to the next cycle's raw content buffer.
   - The chosen concern text is appended to the buffer as a marker.
   - Perceptor keeps running normally — audio (and any Pay Attention images) accumulate into the buffer as the user answers.
   - The user clicks **Hope I answered your question**.
   - The marker text *"Hope I answered your question"* is appended to the buffer.
   - The next cognitive cycle fires immediately, consuming the buffer that now contains the question, the user's answer, and the closing marker.

Only one On-Live Q&A is active at a time; the next can fire only after the current is closed.

---

## 10. End Session

When the user presses **End Session**:

1. Perceptor stops accepting new audio. This is a session-module/runtime boundary only; it does not imply that the operating system microphone is being enabled/disabled here.
2. Any audio in flight is chunked and transcribed by Asr; the resulting transcript is appended to the raw content buffer.
3. The buffer (every transcript / image examination since the previous cycle, including the just-flushed audio) drives one **final cognitive cycle** through `IdeaReflector → Inquirer`.
4. Open concerns are moved to the concern archive.
5. Control passes to the bundled post-performance pipeline (§11–12).

The wrist timer never auto-presses End Session — see GAME_DESIGN §5.1. On time-out the timer switches to penalty time and keeps counting until the user presses the button.

---

## 11. Final Q&A

Final Q&A is gated by the **Final Q&A** session toggle.

1. If Final Q&A is **disabled**, skip directly to §12.
2. **Clarification** — the runtime hands the concern archive plus any concerns still open at end-of-session to the Clarification role, which runs Revise (deduplicate + type merge) then Resolve (per-chunk transcript walk to retire ids the talk already answered). Output: §11.1. The unresolved revised questions drive the Final Q&A panel.
2b. **Seed Inquirer** — call `Session.LoadConcernsForFinalQA(clarification)` before the first round. `EndLivePhase` empties the Inquirer's concern list when it archives; without this call the restricted cycles in step 4 would see no open concerns and could not track which answers resolve which questions.
3. **Final Q&A loop** — the game shows a floating panel with one unresolved question at a time and the buttons **Next** (when more questions remain) and **Finish**. Perceptor restarts so audio and images can be captured during the user's answers.
4. Each round:
   - The runtime appends the asked question text to the raw content buffer as a marker when the panel is shown.
   - User answers; audio chunks accumulate in the buffer (Pay Attention can also append image examinations).
   - When the user presses **Next** or **Finish**:
     - The runtime appends a closing marker to the buffer.
     - One cognitive cycle runs through `IdeaReflector → Inquirer`.
     - **Inquirer is restricted** — the per-call prompt instructs the model to leave `new_concerns` empty; reserved ids stay unused. Removals (`resolved` / `irrelevant`) remain allowed. The schema does not change.
   - **Next** advances to the next revised question.
   - **Finish** closes the panel and proceeds to §12.

If the user presses **Next** on the last revised question the panel auto-finishes — behaviour identical to **Finish**.

### 11.1 Clarification output

Revise:

```json
{
  "questions": [
    { "id": 0, "question": "...", "type": "topic_unknown | comprehension_gap | detail_request" }
  ]
}
```

When two concerns are merged, the resulting question keeps the lower id and gets the more severe type (`topic_unknown` > `comprehension_gap` > `detail_request`).

Resolve (one call per transcript chunk):

```json
{
  "resolved_ids": [0, 3]
}
```

Empty array when the chunk answers nothing.

---

## 12. Post-Performance Pipeline

After End Session and any Final Q&A, the bundled post-performance pipeline runs sequentially — only one `llama_context` is resident at a time:

```text
TranscriptSummarizer
  ↓
IdeaComprehension           (always — populates AudienceSideRecall.Thesis from the cumulative main_idea_understanding)
  ↓
GroundTruthSummarizer       (only if GT file attached)
  ↓
MainIdeaComparator          (only if GT ran — 1 thesis call + N per-claim coverage calls)
  ↓
DeepDive                    (WithAnchor when GT ran, NoAnchor otherwise)
```

IdeaComprehension runs before GroundTruthSummarizer so the `AudienceSideRecall` is fully consolidated (topic + thesis + final `main_idea_understanding`) by the time the comparator opens; GT no longer takes an audience seed, so there is no inter-dependency in the other direction.

### 12.1 TranscriptSummarizer

TranscriptSummarizer extracts a structured representation of how the presentation was delivered, based only on the transcript. It does not assign final scores, compare transcript data with Inquirer output, or simulate understanding. The inferred main idea preserves every claim, fact, and support pillar from the retellings within a 250-word cap; the model never sees the audience-side recall.

#### Pipeline

```text
Transcript store
  → Chunk Builder (ST ≈ 500 tokens, sentence-aware)
  → MAP per chunk
  → REDUCE
  → Structured transcript summary
```

#### Chunking strategy

Accumulates transcript segments until both:

1. total tokens reach the segment threshold ST (configurable; `SessionConfig.SummarizerTokenBudget`, default `500`)
2. the last segment has `chunk_completed = true`

Preserves semantic boundaries; avoids cutting mid-sentence.

#### MAP

Per chunk, MAP runs in **two single-purpose passes** under separate conversations. Mixing free-text generation with enum tags in one schema was unreliable on a 4 B model under constrained decoding (the model either rambled past the decode budget or silently skipped the free-text field). Splitting them keeps each schema trivially structured.

1. **Retelling pass** — fresh `llama_context`, no prior context. Tool: `report_chunk_retelling`. Output:

```json
{
  "retelling": "compressed retelling of this chunk in the speaker's register (1–3 sentences, ≤ 80 words)"
}
```

`retelling` is in the speaker's register, not third-person commentary; rambling content is preserved, not cleaned up.

2. **Signals pass** — fresh `llama_context`. Prior chunks' retellings are re-injected as text so the model can judge consistency relative to what came before. Tool: `report_chunk_signals`. Output:

```json
{
  "structure": "intro | development | conclusion | unclear",
  "consistency": "consistent | minor_drift | major_drift",
  "support": "none | weak | moderate | strong",
  "notes": ["optional short observation"]
}
```

The first chunk's signals prompt header biases `structure` toward `intro` when introductory framing is present.

The runtime combines the two outputs into a single combined per-chunk MAP record before REDUCE consumes it. If either pass fails after retries, that chunk is dropped from the MAP set with a warn; REDUCE proceeds with whatever chunks succeeded.

#### REDUCE

```json
{
  "inferred_main_idea_from_transcript": "what the talk appears to be about"
}
```

`inferred_main_idea_from_transcript` is capped at 250 words; every claim, fact, and support pillar visible in the retellings is preserved, with wording compressed to fit the cap. The per-chunk MAP signals (structure / consistency / support enum tags + retelling) are not aggregated by REDUCE — they are consumed directly, per chunk, by the per-criterion DeepDive sub-roles (§12.5, §13).

### 12.2 GroundTruthSummarizer

If a ground-truth document is attached, the runtime first normalizes its `GroundTruthDocInput` during finalization (per §4.5 — `.txt`/`.md` by `FilePath`, `.jpg`/`.png` and PDF page 1 by `RasterizedImageBytes`). The resulting text is then fed through a **map-reduce** pipeline that produces a flat thesis + claim list — there is no domain-keyed structure on the GT side.

#### Pipeline

```text
Normalized text
  → Sentence Splitter (regex on .!?)
  → TranscriptChunkBuilder (ST ≈ 500 tokens, sentence-aware)
  → MAP per chunk (with prior accumulated claims as context)
  → REDUCE
  → { main_thesis, claims[] }
```

#### MAP

Per chunk, fresh `llama_context`. MAP is **append-only with deduplication context**: each chunk's prompt embeds the **prior accumulated claim list** so the model can avoid restating already-captured claims and emit only what is genuinely new in this chunk.

Tool: `report_chunk_claims`. Output:

```json
{
  "claims": [ "one self-contained factual claim per string" ]
}
```

The prompt asks for every distinct factual claim in the chunk — facts, definitions, comparisons, recommendations, statistics — that is not already in the prior list. Empty array is correct when the chunk adds nothing new. The runtime appends each emitted claim to a flat in-memory accumulator after parsing.

#### REDUCE

Receives the accumulated claim list as bullets and produces:

```json
{
  "main_thesis": "≤ 50 words; the overarching message of the document (gist rule applies)",
  "claims": "plain text, one claim per line; total ≤ 250 words; all distinct claims preserved, wording compressed"
}
```

REDUCE infers `main_thesis` from the accumulated claims directly (no prior per-chunk theses). For `claims`, it deduplicates near-duplicates while preserving every distinct fact; the runtime splits the returned string on newlines into `IReadOnlyList<string>` for downstream consumption.

The structured output `{ MainThesis, Claims }` is fed directly to §12.4 as the anchor decomposition. If no GT file is attached, normalization fails, the normalized text is empty, or REDUCE fails, this stage is skipped and §12.4 does not run; DeepDive falls back to its no-anchor variant.

### 12.3 IdeaComprehension

A single one-shot call. Tool: `report_idea_thesis`. Input: the topic + the cumulative `main_idea_understanding` paragraph built by System2 across the live phase. Output:

```json
{ "thesis": "≤ 50 words; the overarching message of the talk as the listener understood it" }
```

The runtime writes the returned thesis back into `AudienceSideRecall.Thesis`, which is consumed by §12.4 (thesis-comparison call) and §12.5 (DeepDive prefill).

This step runs **before** GroundTruthSummarizer and the comparator so the audience-side recall is fully consolidated (topic + thesis + final `main_idea_understanding`) by the time the comparator opens. If the call fails after retries the runtime logs a warn and leaves the thesis empty; downstream renders treat it as not-yet-established.

### 12.4 MainIdeaComparator

All calls are sequential — the architecture keeps a strict single-KV residency model. The comparator runs **only when ground truth is present**; both sides are already decomposed by the time it starts.

#### Step 1 — Thesis comparison (one call)

Tool: `report_thesis_comparison`. Inputs: anchor thesis + audience thesis. Output:

```json
{
  "thesis_comparison": "short prose paragraph anchored on the anchor thesis (≤ 50 words)"
}
```

The model names what the listener got right, what they missed or weakened, and what they emphasised differently — plain language, no bullets, no headers. It does not invent content beyond what the two theses assert. A failed call is logged with a warn and a fixed placeholder string is recorded; the comparator does not abort.

#### Step 2 — Per-claim coverage (N calls)

For each anchor claim emitted by GroundTruthSummarizer, one entailment call (`report_claim_coverage`). The runtime feeds the single anchor claim plus the audience's full `main_idea_understanding` paragraph:

```json
{
  "coverage": "yes | partial | no",
  "evidence": "optional short paraphrase from the main_idea_understanding (≤ 30 words)"
}
```

The model judges only this single anchor claim against the paragraph; other facts the paragraph contains are irrelevant for this call. A failed coverage call is treated as `no` plus a warn — one bad call does not abort the comparator.

#### Step 3 — Recall (programmatic)

```text
covered = Σ over anchor claims: yes ? 1.0 : partial ? 0.5 : 0
Recall  = covered / N      where N = |anchor.claims|
```

An empty claim list yields `Recall = 0.0` (no NaN). Precision and F1 are intentionally not computed — drift signal is captured qualitatively by `thesis_comparison`.

#### Final output

```json
{
  "anchor_thesis": "...",
  "anchor_claims": [ "...", "..." ],
  "audience_thesis": "...",
  "audience_main_idea_understanding": "the cumulative paragraph the comparator judged against",
  "thesis_comparison": "...",
  "claim_coverages": [
    { "anchor_claim": "...", "coverage": "yes | partial | no", "evidence": "optional" }
  ],
  "recall": 0.0
}
```

This record becomes the `MAIN IDEA COMPARATOR` section of the DeepDive prefill (§13.6) when GT ran. When GT was absent the comparator did not run and the DeepDive prefill omits the comparator section entirely (no-anchor variant).

### 12.5 DeepDive

DeepDive is a coordinator over seven per-criterion sub-roles. Each sub-role is a brand-new conversation with its own system prompt, tool schema, and parser; the slot-semaphore residency model means they run sequentially. The seven criterion outputs are composed back into the rubric record:

```json
{
  "main_idea_clarity":     { "value": 0, "verdict": "..." },
  "structure":             { "value": 0, "verdict": "..." },
  "consistency_focus":     { "value": 0, "verdict": "..." },
  "support_justification": { "value": 0, "verdict": "..." },
  "language_quality":      { "value": 0, "verdict": "..." },
  "emotional_delivery":    { "value": 0, "verdict": "..." },
  "qa_handling":           { "value": 0, "verdict": "..." }
}
```

Five core criteria are scored 1–5. `emotional_delivery` is also scored 1–5 — it has no N/A path because every delivery is rated against the topic's demands. Optional `qa_handling` accepts 0–5 (0 = N/A — criterion did not apply); the runtime emits 0 programmatically (without a model call) when no Q&A rounds were captured. Structure / Consistency & Focus / Support & Justification additionally return N/A (programmatic skip, no model call) when there were no transcript chunks at all.

Per-sub-role tool names: `report_main_idea_clarity`, `report_structure`, `report_consistency_focus`, `report_support_justification`, `report_language_quality`, `report_emotional_delivery`, `report_qa_handling`. Each tool schema returns one `{ value, verdict }` object.

`main_idea_clarity` is the only sub-role with two prompt variants:

- **WithAnchor** (GT ran) — anchored on the comparator output plus a deterministic suggested score derived from Recall (§13.7). The model starts from the suggested score and applies thesis-gate adjustments (divergent thesis caps at 2; partial/mixed thesis caps at 3) plus a confusion-dynamics adjustment. Per-claim `evidence` excerpts ground the verdict in concrete language from the audience's `main_idea_understanding`.
- **NoAnchor** (no GT attached) — anchored on the qualitative match between `inferred_main_idea_from_transcript` and the rendered audience-side recall, plus the confusion-dynamics signal.

The other six sub-roles each have a single system prompt; their inputs and skip rules are documented in §13.

**Deterministic score overrides.** Four sub-roles have their `value` computed deterministically by the runtime from chunk-label distributions and overridden post-hoc, preserving only the model's verdict text:

- `structure.value` from chunk structure labels (§13.7) — **only on the chunks ≥ 3 branch**
- `consistency_focus.value` from chunk consistency labels (§13.7) — **only on the chunks ≥ 3 branch**
- `support_justification.value` from chunk support labels (§13.7) — **only on the chunks ≥ 3 branch**
- `language_quality.value` from chunk grammar labels (§13.3) — unconditional

Structure / Consistency / Support also have a **short-transcript branch** for the 1–2 chunk case: the runtime runs a single shared short-conversation primitive parameterized per criterion (system prompt with an inline level rubric + tool name + parser), fed the shared `DeepDiveShortInput` (raw transcript + chunk retellings + confusion dynamics; see §13.8). The model owns the 1–5 score directly on that branch; no override is applied. With 0 chunks all three sub-roles are skipped programmatically (N/A, no model call).

`main_idea_clarity` receives a suggested score in the prefill but is **not** overridden — the thesis gate is a qualitative judgment only the model can apply.

After each sub-role parses (or is programmatically skipped), the runtime fires `SessionEvents.DeepDiveSubRoleCompleted(role, criterion)` so consumers can tick incremental progress; the terminal `SessionEvents.DeepDiveCompleted(result)` still fires once the seven criteria are composed.

---

## 13. DeepDive Input — Per-Sub-Role Inputs

The DeepDive coordinator builds seven per-sub-role inputs in one pass before invoking any model call. All compression (distributions, dominant labels, phase windowing, slicing) happens in the builder so each sub-role conversation only sees structured, ready-to-render data tightly scoped to its criterion. None of the sub-roles receive raw metric series.

### 13.1 Confusion Dynamics

Shared by Main Idea Clarity, Structure, and Consistency & Focus. Compressed to **5 chronological windows**:

```json
[
  { "phase": "early",     "value": "low" },
  { "phase": "early_mid", "value": "medium" },
  { "phase": "middle",    "value": "medium" },
  { "phase": "late_mid",  "value": "low" },
  { "phase": "late",      "value": "low" }
]
```

If the original series is longer than 5, the runtime splits into 5 windows and picks the dominant value per window (with optional smoothing).

### 13.2 Emotion Dynamics

Consumed by Emotional Delivery.

```json
{
  "overall": "calm | enthusiastic | tense | uncertain",
  "distribution": { "calm": 20, "enthusiastic": 15, "tense": 10, "uncertain": 5 },
  "notes": [
    { "emotion": "enthusiastic | tense | uncertain", "transcript": "short excerpt" }
  ]
}
```

`overall` = most frequent label. `notes` contains every non-calm chunk that has a matching transcript entry — all `enthusiastic`, `tense`, and `uncertain` entries are included, in source order. Calm chunks are kept in the distribution but excluded from notes. Notes are not invented.

The Emotional Delivery sub-role **always runs**. The model commits to a 1–5 score even when the distribution is all calm — a calm delivery is rated against the topic's demands (calm fits a scientific topic; a dramatic / persuasive topic expects variance).

### 13.3 Computed Language Quality

Consumed verbatim by Language Quality.

```json
{ "value": 5, "label": "excellent" }
```

Rule (derived from the chunk grammar-label distribution):

- **excellent → 5** — no `poor` chunks AND `excellent * 2 > good`
- **poor → 2** — `4 * poor > good + excellent`
- **good → 4** — otherwise

Empty distributions fall through to `good`. The Language Quality sub-role takes this value verbatim; the runtime overrides post-hoc if the model writes a different number, preserving only the verdict text.

The sub-role's prefill also includes raw transcript slices for chunks tagged `grammar=poor` or `grammar=moderate`, so the verdict can ground itself in concrete excerpts.

### 13.4 Q&A Spans

Consumed by Q&A Handling. One span per closed Live or Final Q&A round, joined from the runtime's Q&A-rounds history (each entry carries phase, originating concern id, question text, and opened/closed timestamps), the inquirer reflections, and the Clarification result.

```json
[
  {
    "phase": "Live | Final",
    "question": "...",
    "answer": "concatenated transcript chunks falling within the round window",
    "resolution": "Resolved | NotAnswered"
  }
]
```

`resolution` is `Resolved` iff the round's originating concern id appears in the union of all subsequent `inquirer.removed_concerns` with `cause: resolved` ∪ `Clarification.resolved_ids`; otherwise `NotAnswered`.

The Q&A Handling sub-role is **skipped programmatically** when there are no closed rounds; the runtime emits `value: 0` with a fixed reason without a model call.

### 13.5 Main Idea Comparator

The full record from §12.4 — `anchor_thesis`, `anchor_claims`, `audience_thesis`, `audience_main_idea_understanding`, `thesis_comparison`, `claim_coverages` (one entry per anchor claim with `coverage` + optional `evidence`), `recall`. Present only in the Main Idea Clarity sub-role's WithAnchor variant; absent (entirely, not stub-rendered) in the NoAnchor variant.

### 13.7 Computed Scores (Structure / Consistency & Focus / Support & Justification / Main Idea Clarity)

Four sub-roles receive a deterministic `{ value, label }` score in their prefill under `=== COMPUTED SCORE ===`. For Structure, Consistency, and Support this applies only on the **chunks ≥ 3 branch**; the runtime overrides the model's value with the computed one post-hoc, preserving only the verdict text. On the 1–2 chunk branch these three sub-roles run a short-transcript variant instead (see §13.8) and no computed score is rendered. For Language Quality the override is unconditional. For Main Idea Clarity, the score is shown as a suggestion only — the model may adjust it per the thesis-gate rules.

```json
{ "value": 4, "label": "<reason, e.g. 'strong(6) >= moderate+weak(5)'>" }
```

**Structure** — derived from intro/development/conclusion/unclear chunk counts:

- **5** — intro AND conclusion present, no `unclear` chunks, canonical order (last intro index < first conclusion index)
- **4** — intro AND conclusion present, no `unclear` chunks, non-canonical order
- **3** — intro AND conclusion present with `unclear` chunks
- **2** — intro OR conclusion missing
- **1** — neither intro nor conclusion present

**Consistency & Focus** — derived from `consistent` (c), `minor_drift` (m), `major_drift` (M) counts. Cascade most-severe first:

- **1** — `M + 2*m > 2*c`
- **2** — `M + 2*m > c`
- **5** — `M == 0` and `c > 3*m`
- **4** — `M == 0` and `c > 2*m`
- **3** — otherwise (middle zone)

**Support & Justification** — derived from `strong` (s), `moderate` (m), `weak` (w) counts. `none` is merged into `weak`. Cascade most-severe first:

- **1** — `s == 0` and `m == 0` (all weak)
- **2** — `s + m < w` (weak strongly dominates)
- **5** — `s > 2*(m + w)` (strong dominates very heavily)
- **4** — `s >= m + w` (strong matches or exceeds moderate+weak)
- **3** — otherwise (middle zone)

**Main Idea Clarity** (WithAnchor only — suggestion, not enforced) — derived from `Recall`, calibrated against immediate-listener recall norms (lecture-video ~60 % immediate retention; retrieval-practice ~75 %):

- **1** — Recall < 0.30
- **2** — 0.30 ≤ Recall < 0.50
- **3** — 0.50 ≤ Recall < 0.65 (typical immediate-recall band)
- **4** — 0.65 ≤ Recall < 0.80
- **5** — Recall ≥ 0.80

### 13.6 Per-sub-role prefill formats

Each sub-role conversation receives **structured prose** with `=== HEADER ===` markers, indented bullets, and `key: value` lines — one formatter per sub-role, each emitting only the sections that sub-role needs. Section ordering puts the most decision-relevant content closest to the prompt tail, where local attention is most reliable.

#### Main Idea Clarity (WithAnchor variant shown — NoAnchor omits the comparator section entirely)

```text
=== COMPUTED SCORE (based on Recall) ===
  Score: 3
  Label: recall=0.50

=== INFERRED MAIN IDEA FROM TRANSCRIPT ===
...

=== AUDIENCE-SIDE RECALL ===
Topic: Sleep science
Thesis: Sleep helps memory and immunity.
Main idea: Sleep is a biological necessity that supports memory consolidation and the immune system. The speaker emphasised that sleep loss harms both. ...

=== CONFUSION DYNAMICS ===
  - early: low
  - early_mid: medium
  - middle: medium
  - late_mid: low
  - late: low

=== MAIN IDEA COMPARATOR ===
  Anchor thesis: Sleep is foundational to health.
  Audience thesis: Sleep helps memory and immunity.
  Thesis comparison: Audience captured the central thesis but missed the duration nuance.
  Per-claim coverage:
    - [yes] Sleep consolidates declarative memory. — evidence: "the paragraph mentions memory consolidation"
    - [partial] Sleep loss reduces T-cell counts. — evidence: "the paragraph mentions immunity but not T-cells"
    - [no] Adults need 7-9 hours of sleep.
  Recall: 0.50
```

#### Structure / Consistency & Focus

```text
=== COMPUTED SCORE ===
  Score: 4
  Label: intro + conclusion present, non-canonical order

=== CHUNK STRUCTURE LABELS ===            (or === CHUNK CONSISTENCY LABELS ===)
Chunk 1 [intro]: ...
Chunk 2 [development]: ...
...

=== CONFUSION DYNAMICS ===
  - early: low
  ...
```

#### Support & Justification

Same shape but tagged with the support label per chunk; no confusion-dynamics section.

```text
=== COMPUTED SCORE ===
  Score: 4
  Label: strong(6) >= moderate+weak(5)

=== CHUNK SUPPORT LABELS ===
Chunk 1 [moderate]: ...
Chunk 2 [strong]: ...
...
```

#### Language Quality

```text
=== COMPUTED LANGUAGE QUALITY ===
  Score: 4
  Label: good

=== POOR/MODERATE-GRAMMAR TRANSCRIPT SLICES ===
  - <chunk excerpt>
  ...
```

#### Emotional Delivery

```text
=== INFERRED MAIN IDEA FROM TRANSCRIPT ===
...

=== EMOTION DYNAMICS ===
  Overall: calm
  Distribution: calm=20, enthusiastic=15, tense=10, uncertain=5
  Notes (expressive moments only — calm chunks excluded):
    - [tense] short transcript excerpt
    - [enthusiastic] short transcript excerpt
```

#### Q&A Handling

```text
=== Q&A ROUNDS ===
<begin_qa round="1" phase="Live" resolution="Resolved">
  Question: ...
  Answer:
    ...
</end_qa>
<begin_qa round="2" phase="Final" resolution="NotAnswered">
  Question: ...
  Answer:
    ...
</end_qa>
```

All compression is done by runtime code, not by any sub-role. Where a value is fully determined by a computed input (`computed_language_quality`, `recall`), the sub-role is instructed to write a verdict that justifies it; the runtime override on Language Quality enforces this even when the model strays.

### 13.8 Short-Transcript Input (Structure / Consistency & Focus / Support & Justification)

When `chunk_count < 3`, the deterministic ladders for Structure, Consistency & Focus, and Support & Justification cannot discriminate at this scale, so the three sub-roles switch to a shared short-transcript path. The runtime builds **one shared input** in the DeepDive input pass and runs **one shared conversation primitive** parameterized per criterion (system prompt with an inline level rubric + tool name + parser). The model picks the 1–5 score directly against the rubric; no computed override is applied.

Input shape:

```json
{
  "raw_transcript": "concatenated TranscriptStore text, chronological order",
  "chunk_retellings": ["chunk 1 retelling", "chunk 2 retelling"],
  "confusion_dynamics": [
    { "phase": "early", "value": "low" },
    { "phase": "late",  "value": "medium" }
  ]
}
```

Prefill format (identical across the three criteria; only the system prompt's rubric differs):

```text
=== RAW TRANSCRIPT ===
<concatenated transcript text>

=== CHUNK RETELLINGS ===
Chunk 1: <retelling>
Chunk 2: <retelling>

=== CONFUSION DYNAMICS ===
  - early: low
  - late: medium
```

Sections fall back to a placeholder line (`(no transcript)`, `(no retellings)`, `(no reflections)`) when the corresponding store was empty, so the prompt shape is stable even on degenerate runs.

The per-criterion system prompts each carry the criterion's level rubric for the 1–5 scale (Structure judges intro/conclusion presence and overall shape; Consistency & Focus judges drift severity; Support & Justification judges whether claims are backed by reasoning, examples, or evidence). With **0 chunks** all three sub-roles are skipped programmatically — the runtime records `value: 0` with a fixed reason and no model call is made.

---

## 14. Memory Model

Every conversation in the system allocates a fresh `llama_context` per call and disposes it after parsing — no role holds a long-lived KV cache. VRAM residency at any moment is approximately:

```text
Model Weights + 1× KV + mmproj + VR render heap
```

- **KV** — exactly one `llama_context` resident at a time, sized from the engine context-window setting (default 8 K tokens). Enforced both by §7's input-pause sequencing and by an engine-wide slot semaphore that gates conversation creation. Per-call usage varies; the allocation is always sized for the engine window so the CUDA pool can recycle the same block across roles.
- **mmproj** — multimodal projection weights loaded into the engine when an mmproj path is configured. They stay resident for the engine lifetime and are not unloaded between live and post-performance phases.

Post-performance stages run sequentially with the same residency profile.

---

## 15. Why Inquirer Must Not See Transcript

Inquirer must remain blind to the raw transcript.

It sees only:

- IdeaReflector retellings — compact factual records of what was said each cycle
- the current main idea understanding
- the current open concerns

This matters because the goal is to simulate:

- what a listener would understand
- what a listener would miss
- what a listener would remain confused about

Retellings are processed distillations, not verbatim transcripts. If Inquirer could query the raw transcript directly, it would shift from listener simulation to direct text analysis, and the audience-side recall would no longer represent genuine understanding gaps.

---

## 16. Transcript and Metrics Storage

Outside the live reasoning loop, the system stores raw artifacts for final evaluation.

### Stored artifacts

- full transcript chunks (from `AsrConversation` `transcript_of_this_chunk` fields)
- image examinations
- clarity values
- emotion values
- grammar values
- sentence completion flags
- timestamps / order
- Q&A transcripts (audio captured during Live and Final Q&A rounds is chunked through Asr and stored alongside the rest of the transcript stream)
- closed Q&A rounds (phase, originating concern id, question text, opened/closed timestamps; consumed by the post-performance Q&A Handling sub-role to slice answer spans and decide resolution)
- Inquirer reflection history (every reflection, full)

This store is **not** the live source of understanding during the session. It exists as input to the Summarizer pipeline and as reference data for DeepDive inputs derived after the live phase.

---

### 16.1 Tool-call resilience

Every role except Inquirer wraps its model call in a retry loop: up to 3 attempts as configured; if all fail (parse error, no tool call, malformed arguments, schema mismatch), thinking is forced off and 3 more attempts run; if all 6 fail, a `ToolCallFailureException` is raised. Inquirer is exempt — a single bad cycle leaves concerns unchanged. Each retry attempt is logged through the existing per-conversation warn callback.

The session orchestrator catches the exception, raises `SessionEvents.SessionFailed` with the role and last error, then propagates so partial post-performance results are never surfaced.

---

## 17. Out of Scope

- **Session persistence / crash recovery.** All conversation state lives in memory.
- **Parallel inference.** Decoding is strictly sequential within and across cycles.
- **Aggressive concern clustering in Clarification.** Clarification deduplicates and lightly normalizes archived concerns; it does not heavily merge.
- **Rich ground-truth ingestion.** Supported inputs are `.txt`/`.md` by path plus single rasterized image input (`.jpg`/`.png` and PDF page 1) routed through I2t. Multi-page PDFs (beyond the first page), multi-file ingestion, and OCR for image-based PDFs are deferred.

---

## 18. Why This Architecture

- **It evaluates communication, not raw text.** The audience-side recall and the post-performance comparator together capture the gap between what was said, what was understood, and what was intended.
- **The audience-side state is split by cognitive load.** Each cycle: a cheap System1Reactor (no thinking) emits a factual retelling, then a thinking-on System2Reflector integrates it into the cumulative `main_idea_understanding`. Splitting the work matches how a listener processes (recognise / integrate) and keeps each schema small enough that a 4 B local model produces well-calibrated output under constrained decoding (§2.1.2).
- **The audience model is an ideal attentive listener.** Compresses wording but does not forget content. The `main_idea_understanding` is plain text capped at 250 words; every claim and support pillar is preserved, only phrasing is tightened to fit the cap, so coverage scores measure speaker performance, not modelled inattention drift (§2.1.1).
- **Inquirer handles concerns, exclusively.** Inquirer reads all retellings plus the rendered audience-side recall, but never the raw transcript — it must remain a listener simulator, not a transcript analyser (§15).
- **Image input is buffered, not interruptive.** Images do not trigger cycles. They are interleaved into the raw content stream alongside transcripts so each cycle sees a coherent multimodal snapshot of one speaking interval.
- **Comparison happens after the fact, with an anchor.** The audience side is finalised by IdeaComprehension into `(topic, thesis, main_idea_understanding)`. The anchor side is `(main_thesis, claims[])` from GroundTruthSummarizer. The comparator runs one thesis-comparison call plus one per-claim coverage call asking whether the audience paragraph supports each anchor claim (`yes` / `partial` / `no`). Each call is small and locally judged — well-suited to a 4 B model under a 512-token sliding window. When no anchor is available the comparator does not run; the Main Idea Clarity sub-role judges from the audience-side recall, the inferred main idea from the transcript, the chunk retellings, and confusion dynamics instead.
- **Final judgment is independent and per-criterion.** DeepDive runs as seven sub-roles, each a brand-new conversation with a compressed prefill sized for local attention and scoped to one rubric criterion. Splitting the rubric across small targeted calls keeps every per-call prefill short enough for a 4 B model's 512-token sliding window.
- **It stays feasible on a small local model.** Compact enum-based outputs, periodic cycles instead of constant heavy reasoning, sequential conversations with single resident KV, a fresh final judge with a fitted-to-attention prefill.

---

## 19. Final Summary

GemmaStage uses a layered session architecture:

- **Perceptor** (Asr + I2t) perceives the talk in real time
- **CognitiveCoordinator** drives `IdeaReflector → Inquirer` cycles
- **TranscriptSummarizer + GroundTruthSummarizer + MainIdeaComparator** turn what was said and what was intended into a comparable artifact
- **DeepDive** judges the gap against a fixed rubric

The most important property of the system:

> It does not ask whether the idea was correct.
> It asks whether the idea was communicated clearly enough to be understood.
