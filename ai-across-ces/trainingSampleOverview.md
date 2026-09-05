# Training Sample Overview — Recommended Tabs & Demos

*SME recommendation to the human team: which of the four sample approaches to build into the interactive training platform, in what order, and why each one matters.*

**Status:** Recommendation for review
**Source material:** [sampleApproaches.md](sampleApproaches.md)
**Platform:** [poc-interactive-training](../poc-interactive-training/README.md) — 15 scenes exist today; these would be scenes 16+.

---

## Recommendation in one line

Build **four** new tabs. The COBOL one is the flagship and should be built first; the other three are cheap because they reuse machinery the platform already has.

---

## The four tabs

| # | Tab | What it proves | Reuses | Build cost |
| --- | --- | --- | --- | --- |
| 16 | **COBOL → Java: does grounding change the output?** | Two grounding strategies, graded by an executable oracle | Scene 15 A/B harness | High |
| 17 | **Ticket quality gate** | A bad JIRA fails before an agent ever runs | Scene 8 rubric scoring | Low |
| 18 | **Cloud design trap hunt** | An agent finds planted IaC security and cost defects | Scene 13 trap machinery | Low |
| 19 | **Drift scorecard vs golden baseline** | The same agent, scored across three layers, over time | Scenes 10 + 12 | Medium |

---

## 16 — COBOL → Java: does grounding change the output? **(build this first)**

The hardest problem in the source material and the one worth the most. It is also the only one where the answer can be **executed rather than argued**.

**Layout — four boxes plus a verdict:**

1. The `PAYROLL` COBOL program, editable.
2. **Arm A** — raw COBOL, no grounding. Java out.
3. **Arm B** — COBOL + AST/DDG/PDG artifacts. Java out.
4. **The oracle** — GnuCOBOL compiles and *runs* the COBOL over a fixed input set, capturing true outputs.
5. **Differential verification** — execute each arm's Java against those same inputs. Pass/fail per arm. No LLM judge involved in the correctness call.

**Why this matters:** every other lesson on the platform grades with a model. This one grades with a compiler and a test run. It is the strongest evidence artifact we have, and it directly answers the question leadership will ask about a 62M-line migration: *does the extra pipeline work pay for itself?*

**What the learner does:** predicts which variables land in the DDG before it is revealed, then maps each COBOL paragraph to its Java method in each arm.

**Blocker — resolved.** The download at `C:\Users\tkrro\Downloads\gnucobol-3.2_win` is a **source distribution** with no `cobc.exe`. A prebuilt GnuCOBOL 3.2rc1 (MinGW x64) from [mridoni/gnucobol-binaries](https://github.com/mridoni/gnucobol-binaries/releases) is now installed at `C:\Users\tkrro\tools\gnucobol-3.2rc1` and the full pipeline is verified end to end — COBOL → C → executable → `FINAL PAY: 0950.00`, which is arithmetically correct. Working commands and the non-obvious `COB_CONFIG_DIR` fix are in [training-fixture/cobol/README.md](../poc-interactive-training/training-fixture/cobol/README.md).

**Correction to the source material — read before designing Arm C.** [sampleApproaches.md §2.2](sampleApproaches.md) claims the intermediate C gives the model "flattened control flow" and "explicit memory maps." Running the real compiler shows otherwise. Actual `cobc -C` output:

```c
if (((int)cob_cmp_numdisp (b_17 + 5, 2, 40LL, 0) > 0))
  goto l_5;
cob_decimal_set_field (d_0, &f_20);
cob_decimal_mul (d_0, dc_1);
```

- Control flow is **`goto` chains**, not structured blocks — arguably *less* legible than the COBOL `PERFORM` it replaced.
- Arithmetic is `cob_decimal_*` runtime calls, not native operators.
- Data is anonymous byte-offset buffers (`b_17 + 5`), not the named structs the sample document shows.
- The "LLMs know C well" argument does not transfer: this is libcob runtime IR, which is not in any training distribution either.

**So reframe Arm C.** Its value is not legibility — it is that the compiled program **runs and produces ground truth**. Present it as the **test oracle** that grades Arms A and B, not as a third grounding strategy. That claim is defensible and demonstrable; the legibility claim is not.

**Scope discipline:** one program, three arms. Do not add a second COBOL program until the first proves the harness.

---

## 17 — Ticket quality gate

**Layout:** a JIRA ticket, editable · a completeness score against a fixed rubric · a pass/fail gate · the same ticket rewritten to pass.

**Why this matters:** this is the cheapest control in the whole framework. Most agent failures we will see are underspecified tickets, not weak models. Showing a team that a ticket *fails a gate* changes behaviour faster than any lecture on prompting.

**Build note:** rubric scoring already exists in scene 8. This is a new prompt and a threshold, not new machinery.

---

## 18 — Cloud design trap hunt

**Layout:** a short Terraform snippet with three planted defects — a `roles/owner` grant, no budget alert, an undocumented public IP · the agent's findings · a graded result against the sealed trap list.

**Why this matters:** it teaches the five-pillar audit by having people *use* it, and it repeats the platform's most effective pattern — plant known defects, score what was found. It also demonstrates the failure mode people underestimate: an agent confidently citing a deprecated cloud feature.

**Build note:** scene 13 already plants traps and grades against a sealed list. This is a new fixture, not a new mechanism.

---

## 19 — Drift scorecard vs golden baseline

**Layout:** a golden ticket + frozen commit · an agent patch · three scored layers — functional (tests), structural (AST diff vs the human patch), semantic (trace length and goal fidelity).

**Why this matters:** it makes drift concrete. "The agent now takes 40 steps to do what took 5" is a number a manager can act on. It is also the only tab that argues for pinning model versions, which is the control most teams skip.

**Build note:** start with **one** golden case, not the 30–50 the source material calls for. One case proves the scorecard; the registry is a programme, not a lesson.

---

## Priority and sequencing

1. **16 (COBOL)** — highest value, highest cost, and it has a toolchain dependency. Start it now so the GnuCOBOL question is resolved early.
2. **17 (Ticket gate)** — ship alongside 16. Cheapest behaviour change per hour spent.
3. **18 (Trap hunt)** — fixture work only.
4. **19 (Drift)** — last. It needs a golden case curated by someone who knows the codebase, which is a people dependency, not an engineering one.

---

## What I would not build

- **A second COBOL program** until the first harness is trusted.
- **The human-vs-AI sorting exercise.** Fold that table into tab 18 as a panel. It does not carry a tab on its own.
- **A 30–50 case golden registry** as part of a lesson. That is production QA work; the lesson needs one case.
- **Any tab that is graded only by a model.** We already have those. The value of this batch is that three of the four can be scored deterministically.

---

## The honest caveats to state up front

- Tab 16 compares grounding strategies on **one small program**. It demonstrates a method; it does not measure a 62M-line migration. Say so on the slide.
- Differential verification proves the Java matches the COBOL's behaviour on **the inputs we chose**. Boundary cases we did not think of are not covered.
- The intermediate-C legibility claim in [sampleApproaches.md §2.2](sampleApproaches.md) does not survive contact with the compiler. Correct it there before anyone builds a lesson on it.
- The external sources in [sampleApproaches.md §4.4](sampleApproaches.md) are unverified pointers. Fact-check anything before it appears in a lesson.
