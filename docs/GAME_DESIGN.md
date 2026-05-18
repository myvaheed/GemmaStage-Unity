# 🎮 **GemmaStage — Game Design Document**

---

# 1. 🎯 Purpose

This document defines:

* the **player experience**
* the **interaction model**
* the **session flow**
* the **rules of behavior for UI, NPCs, and interruptions**

This document describes **how the game feels and behaves**, not how the system is implemented
(see `docs/SESSION_ARCHITECTURE.md` and `IMPLEMENTATION_PLAN.md` for system-level details).

---

# 2. 🧠 Core Experience

GemmaStage is a **simulation of explaining ideas under pressure**.

The player:

* stands in front of an audience
* explains an idea out loud
* is interrupted with questions
* finishes under time pressure
* receives structured feedback

> The system does not judge the idea — it tests whether the idea was understood.

---

# 3. 🎮 Core Player Loop

```text
Enter environment
      ↓
Configure session
      ↓
Start presentation
      ↓
Explain idea (speech + visuals)
      ↓
Handle interruptions (optional)
      ↓
Finish presentation
      ↓
Handle final Q&A (optional)
      ↓
Receive evaluation
      ↓
Repeat
```

---

# 4. 🧭 Session Flow

---

## 4.1 Start State

The player spawns in the **Lobby** — a small calm, scenic space (warm gradient sky, a soft circular stage-like floor disc, light atmospheric fog, no audience, no boards). The Lobby exists only to host pre-session UI; Stage and Classroom are not loaded yet.

In front of the player, two floating world-space panels:

* **Session Setup Panel** (primary, left) — environment, duration, Q&A modes, Ground Truth Doc, presentation files, echo
* **Game Settings Panel** (secondary, right) — comfort, turning, microphone, language, audio volume, watch hand

Stage or Classroom loads only when the player presses **Start Session**.

---

## 4.2 Interaction Model

The active-hand rule and the ray exist in **both** Lobby and performance phases. Only the ray's default visibility differs.

### Active-hand rule (both phases)

* **Default active hand:** right.
* Pressing **Trigger** or the **below face button** (A / X) on either controller claims the active hand. The other hand becomes inactive immediately.
* Only the active hand emits the ray when the ray is enabled.

### Lobby phase (before **Start Session**)

* **Ray is always on** (active hand only).
* Aim at a Lobby panel → press **Trigger** to interact.

### Performance phase (after **Start Session**)

* **Ray is off by default** — keeps the view clean while the player speaks and draws with grabbed markers (markers don't need the ray).
* Player interacts via:
  * voice (always-on mic)
  * drawing on the whiteboard with grabbed markers (no ray needed)
  * **floating world-space buttons/panels** — Pay Attention, Whiteboard Undo, Presentation Prev / Next, Live Q&A, and Final Q&A — can be pressed by either finger poke or the active-hand ray when the ray is on.
* **Controller button bindings during performance:**
  * **Below face button** (A on right / X on left) — claims the active hand; in Performance mode also controls the ray: same-hand press toggles ray on/off; other-hand press switches active hand and turns the ray on (keeps it on if already on).
  * **Upper face button** (B on right / Y on left) — opens the **"End Session?"** confirmation panel (see §7.1). Available on both controllers.
* **Far-panel auto-ray** — when a far floating panel (End Session confirmation, Evaluation, Results) appears, the ray is shown on the current active hand while that panel is visible; when the panel closes, ray behavior returns to the normal performance-mode rules. Near panels/buttons still support both finger poke and ray, but they do not auto-show the ray.
* Ray ownership still follows the active-hand rule — Trigger on the inactive hand transfers the ray.

---

## 4.2.1 Locomotion

* **Move (continuous)** — **left controller stick**. Always available in both phases.
* **Turn** — **right controller stick**. Two modes selected in Game Settings:
  * **Off** (default) — stick turn disabled; player turns physically.
  * **Snap** — fixed-angle snap turns.
* **Teleport** — not in the game.

---

## 4.3 Session Setup Panel

The primary world-space panel in the Lobby. Holds settings scoped to the upcoming session.

---

### 1. Environment

Expandable dropdown list:

* **Stage** (default)
* Classroom

---

### 2. Performance Duration

* Default: **10 minutes**
* Controls: **−** / **+** buttons
* Step: 2 minutes
* Range: 4 – 30 minutes

---

### 3. Live Q&A

* Toggle: **Enabled** (default) / Disabled

When enabled, an adjacent expandable list sets the **Disturbance Level**:

| Level         | Default | Chance of surfacing a question per concern |
|---------------|---------|--------------------------------------------|
| Low           |         | 15 %                                       |
| **Middle**    | ✓       | **30 %**                                   |
| High          |         | 60 %                                       |

> The runtime applies this probability to each open concern produced by the session module before deciding whether to show a live question popup.

---

### 4. Final Q&A

* Toggle: **Enabled** (default) / Disabled

---

### 5. Ground Truth Doc *(optional)*

A reference file representing the intended main idea of the presentation. Used during the Evaluation phase to compare against what the AI understood.

* **Attach** button — opens a system file picker
* Supported formats: `.txt`, `.md`, `.png`, `.jpg`, `.pdf`
* Shows the attached filename + an **×** button to remove it
* If left empty, evaluation proceeds without ground-truth comparison

> Attachment happens immediately: text files are loaded to validate access, images are loaded as attachment artifacts, and PDFs are rasterized at attach time with only the first page retained. During Evaluation, `SessionLayer` maps the attachment into the session module's Ground Truth input contract: text/Markdown stay path-based, while images and PDF page 1 are passed as rasterized image bytes for GroundTruthSummarizer processing.

---

### 6. Presentation

* Text indicator: **"N slides attached"** (0 when empty)
* **Attach** button — opens the **Presentation Picker** popup dialog

**Popup dialog:**

* Scrollable vertical list of attached items, each shown as a thumbnail preview
* **Add file** button at the bottom — supports `.pdf`, `.jpg`, `.png`
* `.pdf` files auto-expand: each page becomes a separate slide entry
* Each item has an **×** overlay button (top-right of the thumbnail) to remove it
* The popup header has an **×** to close the dialog

Files are rasterized to textures **at attach time**: each PDF page is rendered immediately so the popup shows real previews and unrenderable PDFs surface at attach (not at Start Session). Image files are loaded directly. The rendered textures live with the slide entries until Start Session consumes them or the player removes the entry.

---

### 7. Echo

* Toggle: **Disabled** (default) / Enabled
* When enabled, an **Echo Level** slider becomes active next to the toggle
* Routes the player's own microphone input back through headphones with ~15 ms delay and slight reverb, simulating stage acoustics
* The Echo Level control is independent of Master Audio Volume — adjusting one does not move the other.
* Note: Master Audio Volume drives `AudioListener.volume`, which attenuates all Unity audio output including the echo playback source. Zeroing Master Volume will silence echo too. This is expected behavior, not a bug.

---

### ▶ Start Session

**Disabled until an environment has been selected and a microphone is available.** (Stage is the default, so the usual blocker is microphone availability.)

When the button is disabled, hover/focus can show a short tip popup explaining why. In particular, if no microphone is available, the popup explains that a microphone is required before the session can begin.

On press: loads the chosen environment scene, hides the Lobby panels, transitions to performance phase (ray off by default; see §4.2).

---

## 4.4 Game Settings Panel

The secondary world-space panel in the Lobby (right of the Session Setup Panel). Holds settings that are **not session-scoped** — they persist across sessions.

---

### 1. Comfort Vignette

* **On** (default) / Off

---

### 2. Turning Mode

* **Off** (default) — right stick turn fully disabled; player turns physically
* **Snap** — fixed-angle snap turns

---

### 3. Microphone

* Expandable dropdown listing all available system input devices
* A horizontal **volume meter** next to the dropdown shows the active microphone's real-time input level

---

### 4. Language

The language used by the AI for system prompts and generated text:

* **English** (default)
* French
* German
* Spanish
* Turkish
* Italian

Selection is passed to the session layer as part of session setup. Prompt-level language propagation is handled by the session-module wiring task so transcript and user-facing role outputs follow the selected language.

---

### 5. Master Audio Volume

* Horizontal slider
* Affects: background music and UI interaction sounds (button clicks, beeps)
* Drives `AudioListener.volume` — attenuates all Unity audio output including echo playback (see §4.3.7 note)

---

### 6. Watch Hand

* **Right** (default) / Left
* Controls which wrist hosts the timer canvas during performance

---

**Persistence:** all six values are saved via Unity `PlayerPrefs` on change and reloaded on next launch.

Both Lobby panels support short tip popups on hover/focus so the player can quickly understand what each setting does without leaving the scene.

---

# 5. 🎤 Presentation Phase

---

## 5.1 Environment Elements

Both environments (Stage / Classroom) include:

---

### Audience

* up to ~20 NPCs
* randomized:
  * clothing
  * hairstyle
  * skin tone
  * accessories (e.g., glasses)

Behavior:

* mostly static
* subtle idle animations
* no lip sync
* no voice

---

### Whiteboard

* 4 colored markers
* can be picked up and used for drawing

Features:

* free drawing
* **floating Undo button** — positioned next to the whiteboard, pressable by finger poke or the active-hand ray

---

### Presentation Board

* displays slides
* navigation:
  * next
  * previous
* **floating Prev / Next buttons** — positioned on the user-facing side of the slide, pressable by finger poke or the active-hand ray

---

### "Pay Attention" Button

* **One floating button per board** — two in the scene total: one near the Whiteboard, one near the Presentation Board.
* Pressable by finger poke or the active-hand ray (when manually toggled on with A/X).
* Captures the current board content as an image and sends it to the AI for analysis.

---

### Wrist Timer

* Mounted on the wrist specified in Game Settings (§4.4.6)
* **Digital countdown display** (`MM:SS`)

Behavior:

* **Session start** — one regular beep
* **1 minute remaining** — one regular beep
* **Timer reaches zero** — one **distinct beep** (different from the regular beep); timer switches to **count-up mode** rendered in **dark red digits** (penalty time). The session does **not** end automatically — the player keeps speaking until they choose to end.

---

## 5.2 Player Actions

The player can:

* speak
* draw on the board
* switch slides
* highlight visuals
* respond to questions

---

# 6. ❓ Live Q&A (On-Live Mode)

---

## 6.1 General Rules

* Controlled entirely by the **GemmaStage session module**
* Can trigger at **any moment during presentation**
* Only **one active question at a time**
* Surfacing a popup is gated by the **Disturbance Level** (§4.3.3) — the runtime applies the probability (15 / 30 / 60 %) to each candidate concern before deciding whether to show the popup

---

## 6.2 Question Trigger

When a question is surfaced:

* one NPC raises a hand
* a question mark appears above the NPC

---

## 6.3 Question Prompt UI

A floating panel appears in front of the player:

```text
"May I ask a question?"
[Yes] [No]
```

---

## 6.4 Player Response Behavior

### If player presses YES

* question text appears on the same panel
* panel shows:
  * question
  * **"Hope I answered your question"** button

---

### If player presses NO

* panel disappears
* question is skipped
* NPC drops the hand; Q-mark is removed

---

### If player does nothing

* panel automatically disappears after **10 seconds**
* NPC drops the hand; Q-mark is removed

---

## 6.5 Closing a Question

When the player presses **"Hope I answered your question"**:

* current question ends
* NPC returns to Idle; Q-mark is removed
* the system may surface another question later if available

---

## 6.6 NPC Behavior During Q&A

* NPCs do not speak
* no lip movement
* interaction is entirely UI-based

---

## 6.7 Visual Feedback

The only real-time audience signal at launch:

* **hand raising (question intent)**

No:

* facial reactions
* emotional overlays
* real-time confusion indicators

> **Extensibility:** the NPC animation system is designed so additional reactions (e.g., nodding, confused look, attentive lean) can be added as a data-only change. Until that decision is made, hand-raise is the only visible audience signal.

---

# 7. ⏱ End of Presentation

The session does **not** end automatically when the timer reaches zero.

* At timer zero: penalty mode activates (count-up, dark red digits) — see §5.1 Wrist Timer.
* The player keeps speaking as long as they wish.
* The session ends only when the player manually triggers it.

---

## 7.1 Ending the Session

The player presses the **upper face button** on either controller (B on right / Y on left). A floating confirmation panel appears:

```text
"End Session?"
[Yes] [No]
```

* **No** — panel closes; session continues uninterrupted.
* **Yes** — session ends. The flow proceeds to Final Q&A (§7.2) if it is enabled, otherwise directly to the Evaluation phase (§8).

While the confirmation panel is visible, Perceptor stops capturing — no ASR or I2T runs. Pressing **No** resumes Perceptor; pressing **Yes** keeps it paused as the session proceeds to teardown. Live Q&A popups are also suppressed for the duration.

During the Evaluation phase the same upper-button input opens a different panel: **"Cancel Analyzing / Return to Lobby"** (see §8.1).

---

## 7.2 Final Q&A Phase

If Final Q&A is enabled (§4.3.4), the session module first prepares questions during a **Clarification** step. A **"Preparing questions…"** spinner is shown during this brief wait. A **Skip Final QA** button on the spinner lets the player skip Final Q&A entirely and proceed directly to the Evaluation phase.

Once questions are ready, a panel displays them sequentially:

```text
[question text]
[Finish]   [Next]
```

* **Finish** — skips any remaining questions and begins the Evaluation phase immediately.
* **Next** — the player's spoken answer to the current question is committed to the session module, and the next question is shown.

> Final Q&A continues using the same session-runtime audio flow. The session module handles audio chunking internally.

---

# 8. 🧠 Evaluation Phase

---

## 8.1 Analyzing State

After Final Q&A ends (or directly after **End Session: Yes** when Final Q&A is disabled), the **Evaluation panel** appears:

> "Analyzing your presentation…"

with a **percentage progress bar**.

Progress steps:

1. Summarizer MAP
2. Summarizer REDUCE
3. Ground Truth Doc processing (only if a doc is attached — final image examination or text extraction for GroundTruthSummarizer)
4. MainIdeaComparator (only if a Ground Truth Doc is attached)
5. DeepDive evaluation

A **"Cancel Analyzing / Return to Lobby"** button is visible at all times during analysis. Pressing it kills in-flight inference and discards the session entirely — no partial results are preserved.

The panel also exposes a **Dismiss Session / Go to Lobby** button. Pressing it cancels the active inference, disposes the current session, and returns the player to the Lobby — no Results screen will be shown for this run. This is the same affordance the **End Session** button is renamed to during the analyzing wait, so the player always has a way out if the pipeline is taking too long.

---

## 8.2 Results Screen

Once DeepDive completes, the Evaluation panel transitions to the Results Screen.

---

### Core Metrics

* Main Idea Clarity
* Structure
* Consistency & Focus
* Support & Justification
* Language Quality

---

### Feedback

* unclear parts
* logical gaps
* missing explanations

---

### Actions

* **Export as PDF** — saves the evaluation report to disk
* **Exit** — returns to the Lobby (settings panels remember their last values)

---

# 9. 🎨 Visual Style

---

## Style Direction

* stylized
* semi-cartoon
* soft color palette

---

## Design Goals

* reduce GPU load
* avoid uncanny realism
* keep focus on communication

---

## NPC Design

* simple faces (predefined variations)
* minimal animation
* expression through posture only

---

# 10. 🧠 Design Principles

---

## 10.1 Communication Over Gameplay

There is no:

* win condition
* failure state

Only:

> improvement through repetition

---

## 10.2 Safe Practice Space

The player can:

* fail safely
* restart instantly
* experiment freely

---

## 10.3 Pressure Simulation

Pressure is created through:

* time limit
* audience presence
* unexpected questions

---

## 10.4 Multimodal Thinking

The system evaluates:

* speech
* visuals
* structure

---

# 11. 🔁 Replay Loop

```text
Run session
  ↓
Receive feedback
  ↓
Improve explanation
  ↓
Run again
```

---

# 12. 🔥 Key Experience Moment

> The system asks a simple question…
> and you realize you cannot answer it clearly.

---

# 13. 🧩 Relationship to System Architecture

* Game layer = **experience**
* Session module = **intelligence**

System details are defined in:

* `docs/SESSION_ARCHITECTURE.md` (session architecture)
* `IMPLEMENTATION_PLAN.md` (implementation plan)

---

# 14. 🚧 Finalized Behavioral Rules (Important)

---

### Q&A Rules

* Only one active question at a time.
* The next question can appear only after the previous is closed.
* Live Q&A surfacing is gated by the Disturbance Level probability (15 / 30 / 60 % per concern).
* The question prompt **auto-dismisses after 10 seconds** if ignored.
* Questions are generated by the session module.

---

### NPC Rules

* No voice.
* No lip sync.
* Minimal animation.
* The only visible audience signal is hand-raise (§6.7). The animation system supports adding more reactions as a data-only extension.

---

### UI Rules

* **Lobby:** ray on the active hand, always.
* **Performance:** ray off by default.
  * **Below face button** (A on right / X on left) — claims active hand; same-hand press toggles ray on/off; other-hand press switches active hand and turns ray on (keeps it on if already on).
  * **Far-panel auto-ray** — far floating panels (End Session, Evaluation, Results) show the ray on the current active hand while visible, then return to normal performance-mode ray behavior on close. Near panels/buttons (Live Q&A, Final Q&A, Undo, Prev / Next, Pay Attention) support both finger poke and ray, but do not auto-show the ray.
  * **Upper face button** (B on right / Y on left) — opens the "End Session?" confirmation panel.
* The session ends only via "End Session: Yes". Timer reaching zero does **not** auto-end the session.
* The microphone is **always on** from session start through Final Q&A. Game code never turns the mic off.
* Active-hand rule (Trigger and A/X both claim the active hand) applies in both phases. Default active hand: right.
* Floating buttons/panels (Whiteboard Undo, Presentation Board Prev / Next, Pay Attention, Live Q&A, Final Q&A) support both finger poke and the active-hand ray. Only far panels auto-show the ray.
* The End Session confirmation panel suppresses Live Q&A popups while visible and pauses Perceptor capture; closing it via **No** resumes both.

---

### Timer Rules

* **Three audio cues:**
  * Session start — regular beep.
  * 1 minute remaining — regular beep.
  * Time-out — distinct beep (different sound).
* At time-out: digits switch to dark red and the timer counts up (penalty mode). The session continues.

---

### Turning Mode Rules

* Off / Snap. Default: **Off**.

---

### Error Handling

* If the session module raises `SessionFailed` at any phase, the game shows a single floating panel — *"Something went wrong"* with **OK**. Pressing OK runs the same teardown as **Dismiss Session / Go to Lobby** (§8.1): cancels in-flight inference, disposes the session, returns to the Lobby. No Results screen for this run.
