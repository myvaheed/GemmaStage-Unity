# 🎤 Evaluation Criteria for Presentations and Explanations

This system evaluates the **quality of how an idea is communicated**, not the idea itself or its correctness.

It is designed to be **universal** and applicable to:

* public speaking
* teaching sessions
* academic explanations
* interviews and technical discussions

---

# 🛠️ How Scoring Works (Pipeline)

During the **live phase** the session fills three stores:

* **TranscriptStore** — raw ASR text per audio chunk.
* **MetricsStore** — per-chunk Perceptor signals (emotion + grammar).
* **RetellingsHistory** — one entry per CognitiveCoordinator cycle, including the Inquirer's audience-side **confusion score** (Low / Medium / High / VeryHigh).

When the speaker ends the session, the **post-performance pipeline** runs:

1. **Clarification** — settles any concerns still open after Final Q&A.
2. **TranscriptSummarizer** — MAP per chunk (retelling + structure / consistency / support labels) and REDUCE (inferred main idea).
3. **GroundTruthSummarizer** — only when a reference document was attached.
4. **MainIdeaComparator** — only with a reference doc; produces per-claim coverage, thesis comparison, and Recall.
5. **DeepDive** — the seven per-criterion sub-roles below, run sequentially. Each produces one score (1-5) plus a short verdict.

A **chunk** below refers to one TranscriptSummarizer MAP output (roughly one logical paragraph of speech). Whether a sub-role scores deterministically, by model, or by a hybrid path depends on the chunk count and the data available.

## Scoring modes

| Mode                  | What it means                                                                                                          |
| --------------------- | ---------------------------------------------------------------------------------------------------------------------- |
| **Deterministic**     | Score is computed by a fixed rule over per-chunk labels. The model contributes only the verdict prose; the rule's value overrides any number the model proposes. |
| **Model-driven**      | The model picks the score 1-5 freely from the supplied data and the prompt rubric. No override.                        |
| **Hybrid (forked)**   | Deterministic when chunks ≥ 3; model-driven when chunks are 1-2; **N/A** when 0 chunks.                                |
| **Hybrid (seeded)**   | A computed value is shown to the model as a *suggestion*. The model adjusts within published rules but owns the final number. |

## When is a criterion N/A?

| Criterion              | N/A condition                                |
| ---------------------- | -------------------------------------------- |
| Structure              | 0 transcript chunks                          |
| Consistency & Focus    | 0 transcript chunks                          |
| Support & Justification| 0 transcript chunks                          |
| Q&A Handling           | No Q&A rounds occurred                       |
| Main Idea Clarity      | Never (always scored)                        |
| Language Quality       | Never (always scored)                        |
| Emotional Delivery     | Never (always scored)                        |

A skipped criterion is recorded as `NotApplicable` and contributes **0** to the final sum.

---

# ✅ Core Evaluation Criteria (Included in Final Score)

Each criterion is scored on a **1-5 scale**. Five core criteria sum to the final score (max 25).

---

## 1. Main Idea Clarity — *hybrid (seeded with reference / model-driven without)*

**What it evaluates:**
How clearly the core idea is expressed and whether it can be summarized **in a single sentence**.

**Standard rubric:**

**5** — The main idea is clear, specific, and easily expressed in one sentence.
**4** — The main idea is understandable but slightly vague or not sharply defined.
**3** — The general meaning is understandable, but difficult to summarize precisely.
**2** — The topic is somewhat recognizable, but the core idea is unclear or diluted.
**1** — The main idea cannot be identified.

### Case A — Ground Truth document is attached (anchor variant)

The MainIdeaComparator produces per-claim coverage (`yes` / `partial` / `no`), a thesis comparison, and a numerical **Recall**:

```
Recall = (#yes + 0.5 · #partial) / #anchor_claims
```

Recall seeds a suggested score:

| Recall          | Suggested score |
| --------------- | --------------- |
| < 0.30          | 1               |
| < 0.50          | 2               |
| < 0.65          | 3               |
| < 0.80          | 4               |
| ≥ 0.80          | 5               |

The thresholds are grounded in empirical retention research: lecture-video immediate retention sits around 60%, retrieval-practice retention around 75%; a "half-to-two-thirds correct" recall therefore lands at the median **3**.

The model then **adjusts** the suggestion using published rules — the only signals the deterministic step cannot assess:

* Thesis clearly **divergent** (audience's central point disagrees with the reference) → **cap at 2**.
* Thesis **partial / mixed** → **cap at 3**.
* **Many `partial`** covers at the same Recall → **lean one step lower**.
* Persistent **`medium` / `high` / `very_high`** audience confusion → **drop by 1**.

The model owns the final value; no override is applied.

### Case B — No Ground Truth document (no-anchor variant)

There is no reference to compare against, so the model judges from listener-side signals only:

* the **inferred main idea** (from TranscriptSummarizer REDUCE)
* the **audience-side recall** (from IdeaComprehension)
* the **confusion dynamics** across the talk

Substantial alignment between inferred main idea and audience recall, combined with stable-low confusion, yields a high score; divergence or persistent confusion yields a low one. The model picks 1-5 freely.

---

## 2. Structure — *hybrid (forked by chunk count)*

**What it evaluates:**
How logically and coherently the explanation is organized.

**Why it forks.** The standard ladder relies on intro / development / conclusion *shape* across at least three chunks. Below that, label-counting cannot discriminate (a single chunk has no arc to speak of, yet a single coherent sentence can still be perfectly structured). The short-transcript branch therefore asks the model to judge from content directly.

### Case A — `0 chunks` → **N/A**

No model call. The session contained no scoreable speech.

### Case B — `1-2 chunks` → model-driven, no override

The model receives the **raw transcript**, the **chunk retellings**, and the **confusion dynamics**, and applies this short-form rubric:

**5** — Clean, clear structure. Intro, development, and conclusion are all present.
**4** — Good but not perfect. An intro and a conclusion can be felt.
**3** — Intro, development, or conclusion is hard to identify, but the talk still feels coherent.
**2** — Either the intro or the conclusion is clearly missing.
**1** — The talk has no recognizable shape.

### Case C — `≥ 3 chunks` → deterministic ladder

The TranscriptSummarizer MAP step labels each chunk as `intro` / `development` / `conclusion` / `unclear`. The score is then computed by a fixed rule. The model writes the verdict prose; its numeric value is overridden.

| Condition                                                                              | Score |
| -------------------------------------------------------------------------------------- | ----- |
| No `intro` AND no `conclusion`                                                         | **1** |
| Missing `intro` XOR missing `conclusion`                                               | **2** |
| Both present, but any chunk labelled `unclear`                                         | **3** |
| Both present, last `intro` chunk appears *before* first `conclusion` chunk             | **5** |
| Both present, but chunks are out of canonical order                                    | **4** |

---

## 3. Consistency & Focus — *hybrid (forked by chunk count)*

**What it evaluates:**
Whether the explanation stays on topic and develops logically without contradictions.

**Why it forks.** With fewer than 3 chunks, the consistent / minor_drift / major_drift ratio is too coarse to discriminate. The short branch judges from content + confusion trend instead.

### Case A — `0 chunks` → **N/A**.

### Case B — `1-2 chunks` → model-driven, no override

The model receives the raw transcript, chunk retellings, and confusion dynamics. Short-form rubric:

**5** — Every part builds on the previous one and enriches the topic.
**4** — Mostly focused. The speaker drifts occasionally.
**3** — Drifts are clearly noticeable.
**2** — Most of the talk drifts off topic.
**1** — No focus or consistency at all.

### Case C — `≥ 3 chunks` → deterministic ladder

Let `c = #consistent`, `m = #minor_drift`, `M = #major_drift`. Conditions are evaluated most-severe-first; the first match wins.

| Condition                          | Score |
| ---------------------------------- | ----- |
| `M + 2m > 2c`                      | **1** |
| `M + 2m > c`                       | **2** |
| `M == 0` AND `c > 3m`              | **5** |
| `M == 0` AND `c > 2m`              | **4** |
| otherwise                          | **3** |

The weighting (`2m` for minor drifts, no weight on `M` beyond presence) reflects that minor drifts are common in good talks while any major drift is a real coherence break.

---

## 4. Support & Justification — *hybrid (forked by chunk count)*

**What it evaluates:**
Whether statements are supported with reasoning, explanations, or examples.

**Why it forks.** With 1-2 chunks, the strong / moderate / weak ratio is too coarse — a single strong chunk maxes the ladder, a single weak chunk collapses it. The short branch judges from content + confusion trend.

### Case A — `0 chunks` → **N/A**.

### Case B — `1-2 chunks` → model-driven, no override

The model receives the raw transcript, chunk retellings, and confusion dynamics. Short-form rubric:

**5** — Claims are backed by strong reasoning, examples, or evidence throughout.
**4** — Support is present, but more strong evidence would help.
**3** — Some support is present, but it is weak.
**2** — No supporting reasoning or examples.
**1** — No support, and the topic itself sounds fantastical or implausible.

### Case C — `≥ 3 chunks` → deterministic ladder

Let `s = #strong`, `m = #moderate`, `w = #weak + #none` (`none` is folded into `weak`).

| Condition                          | Score |
| ---------------------------------- | ----- |
| All chunks are `weak` (`s+m == 0`) | **1** |
| `s+m < w`                          | **2** |
| `s > 2(m+w)`                       | **5** |
| `s ≥ m+w`                          | **4** |
| otherwise                          | **3** |

---

## 5. Language Quality — *deterministic*

**What it evaluates:**
Grammatical correctness, clarity, and precision of expression.

> ⚠️ Language errors **should not significantly reduce the score** if the meaning remains understandable.

Language Quality is computed deterministically from the per-chunk grammar-label distribution (`poor` / `moderate` / `good` / `excellent`) tagged by the Perceptor. The model writes the verdict prose; the deterministic value overrides whatever number the model proposes.

Let `p = #poor`, `g = #good`, `e = #excellent`. `moderate` chunks are intentionally **unweighted** — they neither push toward excellent nor toward poor.

| Condition                              | Score | Label       |
| -------------------------------------- | ----- | ----------- |
| `p == 0` AND `2e > g`                  | **5** | excellent   |
| `4p > g + e`                           | **2** | poor        |
| otherwise (also: empty distribution)   | **4** | good        |

**Note:** the deterministic rule produces only **5 / 4 / 2**. Scores **3** and **1** are not used for Language Quality — granularity below "good" belongs to other criteria (Clarity, Structure). This is an intentional design choice, not an oversight.

---

# ⭐ Additional Criteria (Not Included in Final Score)

---

## 6. Emotional Delivery — *model-driven, always runs*

**What it evaluates:**
How well tone, pacing, and expressiveness match the context and support communication.

Standard rubric (1-5):

**5** — Delivery enhances understanding and is well-suited to the context.
**4** — Generally appropriate, but not fully effective.
**3** — Neutral; does not impact understanding.
**2** — Detracts from communication (too flat or inappropriate).
**1** — Strong mismatch with the context.

**Always runs.** Earlier versions skipped this criterion when no expressive moments were captured (all-calm distribution); that gate has been removed. Even an entirely calm delivery must be scored against the topic's demands:

* A **scientific / technical** topic benefits from calm, measured delivery → calm = appropriate, high score.
* A **political / dramatic / persuasive** topic benefits from variance → calm = flat = lower score.
* An **`uncertain` cluster** indicates the speaker was unsure, regardless of topic → lower score unless the talk is explicitly exploratory.

The model infers the topic's genre from the **inferred main idea** and weighs the emotion distribution + non-calm expressive notes against it.

---

## 7. Q&A Handling — *model-driven, skipped only when no rounds*

**What it evaluates:**
How effectively the speaker responds to questions.

Standard rubric (1-5):

**5** — Questions are clearly understood and answered directly and accurately.
**4** — Mostly answers well, but not always fully or clearly.
**3** — Partial answers with noticeable gaps.
**2** — Often misses the point or avoids the question.
**1** — Fails to understand or respond meaningfully.

**Skip rule:** if zero Q&A rounds occurred (no Live Q&A and no Final Q&A) → **N/A**, no model call.

Otherwise the model receives each round as `(question, answer-window transcript, resolution flag)`. The resolution flag joins the round's concern id against the inquirer's `resolved` reflections plus the Clarification step's resolved ids. The model may emit `value = 0` to mean N/A itself — used when answers were unintelligible or evasive enough that no useful scoring is possible.

---

# 🧮 Final Score

The final score is the sum of the **five core criteria**:

**Maximum: 25 points**

| Score Range | Interpretation      |
| ----------- | ------------------- |
| 22-25       | Strong performance  |
| 18-21       | Good performance    |
| 14-17       | Average performance |
| 10-13       | Weak performance    |
| 5-9         | Poor performance    |

> **N/A and the final sum.** A skipped core criterion contributes **0** to the sum. With 0 transcript chunks, Structure / Consistency / Support all skip (–15 points). The interpretation table assumes all five core criteria produced a score; if any core criterion is N/A, the total should be read as a lower bound rather than a comparison against typical sessions.

---

# ⚠️ Evaluation Principles

* The system evaluates **how well the idea is communicated**, not its correctness.
* Do **not** assume expert-level knowledge.
* Focus on clarity, structure, and logical consistency.
* A score of **5** should only be given when the criterion is clearly satisfied.
* A score of **3** represents an acceptable, average level.
* When a deterministic rule is in force, the score is **non-negotiable**: the model contributes only the verdict prose. This protects published scores from model variance on signals that can be counted exactly.

---

# 🧩 Short Labels (for UI)

**Core Criteria:**

* Main Idea
* Structure
* Focus
* Support
* Language

**Additional:**

* Delivery
* Q&A
