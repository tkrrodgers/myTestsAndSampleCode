# Training Sample Overview — Recommended Tabs & Demos

*SME recommendation to the human team: which of the four sample approaches to build into the interactive training platform, in what order, and why each one matters.*

**Status:** Recommendation for review
**Source material:** [sampleApproaches.md](sampleApproaches.md)
**Platform:** [poc-interactive-training](../poc-interactive-training/README.md) — 15 scenes existed when this was written; the four tabs proposed here were built as scenes 17, 19, 20 and 21 (the COBOL domain-segmentation scene took slot 18). The platform now has 39 scenes.

---

## Recommendation in one line

Build **four** new tabs. The COBOL one is the flagship and should be built first; the other three are cheap because they reuse machinery the platform already has.

---

## The four tabs

| # | Tab | What it proves | Reuses | Build cost |
| --- | --- | --- | --- | --- |
| 16 | **COBOL → Java: does grounding change the output?** | Three grounding strategies, graded by an executable oracle | Scene 15 A/B harness | High |
| 17 | **Ticket quality gate** | A bad JIRA fails before an agent ever runs | Scene 8 rubric scoring | Low |
| 18 | **Cloud design trap hunt** | An agent finds planted IaC security and cost defects | Scene 13 trap machinery | Low |
| 19 | **Drift scorecard vs golden baseline** | The same agent, scored across three layers, over time | Scenes 10 + 12 | Medium |

---

## 16 — COBOL → Java: does grounding change the output? **(build this first)**

The hardest problem in the source material and the one worth the most. It is also the only one where the answer can be **executed rather than argued**.

**Layout — five boxes plus a verdict:**

1. The CardDemo interest program, editable.
2. **Arm A** — raw COBOL, no grounding. C# out.
3. **Arm B** — COBOL + AST/DDG/PDG artifacts. C# out.
4. **Arm C** — COBOL + compiler-resolved facts: the field table (offsets, digits, scale, sign) and the resolved control flow. C# out.
5. **The oracle** — GnuCOBOL builds and *runs* the COBOL over a fixed case set, capturing true outputs.
6. **Differential verification** — execute each arm's answer against those cases. Pass/fail per arm. No LLM judge involved in the correctness call.

**Use real Enterprise COBOL, not a synthetic fixture.** The fixture is the monthly-interest calculation from [AWS CardDemo](https://github.com/aws-samples/aws-mainframe-modernization-carddemo) (Apache-2.0) — record layouts from copybooks `CVTRA01Y` and `CVTRA02Y`, arithmetic from `CBACT04C` paragraph `1300-COMPUTE-INTEREST`, unchanged. A stdin driver was added so the oracle can run it over a case set.

CardDemo is the right choice over the CMS OPPS Pricer for one reason: **the Pricer drifts.** Its payment rates change quarterly, so a fixture built on it rots and its "correct" answers expire. CardDemo is a stable reference application built for exactly this purpose. (Both CMS Pricer releases do compile cleanly — `pricerqtr4-07` and `pricerqtr4-08` are complete matched sets, 0 errors, 384 `PERFORM THRU` ranges resolved in the 2008 build. Keep that as a scale demonstration, not as the graded fixture.)

**The line that carries the lesson:**

```cobol
COMPUTE WS-MONTHLY-INT = (TRAN-CAT-BAL * DIS-INT-RATE) / 1200
```

No `ROUNDED`, so it truncates. $1,000.00 at 18.99% is exactly 15.825; the program pays **15.82**. A migration that rounds pays 15.83 — one cent, every account, every month. Nothing in the source says "truncate"; the standard says it and the compiler implements it.

Measured: a deliberately wrong implementation still passes **5 of 6** cases. That is what makes migration defects dangerous, and it is worth showing.

**Blocker — resolved.** The download at `C:\Users\tkrro\Downloads\gnucobol-3.2_win` is a **source distribution** with no `cobc.exe`. A prebuilt GnuCOBOL 3.2rc1 (MinGW x64) from [mridoni/gnucobol-binaries](https://github.com/mridoni/gnucobol-binaries/releases) is installed at `C:\Users\tkrro\tools\gnucobol-3.2rc1`. Commands and the non-obvious `COB_CONFIG_DIR` fix are in [training-fixture/cobol/README.md](../poc-interactive-training/training-fixture/cobol/README.md).

**The dialect is not cosmetic — it changes the numbers.** Same program, same input, four dialects:

```
MOVE 50000 TO WS-BIN.   *> PIC 9(4) COMP

default  -> 000000      truncates to 4 decimal digits
ibm      -> 050000      keeps the binary value (TRUNC(BIN))
mf       -> 050000
cobol85  -> 000000
```

Zero versus fifty thousand, silently. `ibm-strict.conf` sets `binary-truncate: no`, `binary-size: 2-4-8`, `binary-byteorder: big-endian`, `hostsign: yes`, `complex-odo: yes` — all of which affect results or acceptance. **The compiler is an oracle only once you tell it which COBOL you mean.** Under the wrong dialect it is a confident, silent liar.

**Open item for the team:** confirm the `TRUNC` option the mainframe build actually uses (`TRUNC(BIN)`, `TRUNC(STD)`, `TRUNC(OPT)` — it is a JCL compile parameter). If production is `TRUNC(STD)` and the oracle runs `-std=ibm`, the oracle and the mainframe disagree and every downstream number inherits it.

### First real run — and what it exposed

Gemma 4 31B, one run per arm, graded by the oracle:

| Arm | Grounding | Cases passed | Prompt tokens |
| --- | --- | --- | --- |
| A | none | **1 / 6** | 503 |
| B | AST / DDG / PDG | **4 / 6** | 893 |
| C | compiler-resolved facts | **4 / 6** | 737 |

Grounding clearly helped: 1/6 to 4/6. But B and C tied, and **both failed on exactly the same two cases** — 15.825 rendered as 15.83 instead of 15.82, and 4.166625 as 4.17 instead of 4.16.

That is not a precision problem, and a bigger numeric type does not fix it. Both values are exactly representable. The arms *rounded where COBOL truncates*.

**The cause was a defect in arm C, not in the idea.** Arm C was being given the field table — offsets, digits, scale, sign — and nothing about arithmetic semantics. It never learned the `COMPUTE` had no `ROUNDED` clause, so it had no more information about the thing that actually mattered than arm B did.

The compiler does record it. `cob_decimal_get_field(d_0, &f_24, 0)` carries a store flag: `0` truncates, `1` rounds. Compiling the same program with and without `ROUNDED` flips the flag and flips the answer from 15.82 to 15.83. Arm C now extracts it:

```
line 21 COMPUTE: result stored into WS-MONTHLY-INT is TRUNCATED toward zero
                - the statement has no ROUNDED clause (11 digits, scale 2, signed)
```

**The lesson generalises beyond this bug:** grounding only helps if you extract *the facts that decide the answer*. Layout facts do not fix an arithmetic defect. Whoever builds an extraction pipeline has to ask what class of error they are trying to prevent, and go and get that specific fact.

### On numeric representation

A bigger integer type is the wrong fix here, but it is the right fix for a different problem. IBM `ARITH(EXTENDED)` allows **31 significant digits**; C# `decimal` holds 28–29 and Java `double` is not a candidate at all. For production Enterprise COBOL, scaled `BigInteger` (or Java `BigDecimal` with an explicit `MathContext` and `RoundingMode.DOWN`) is the defensible target representation — not because of this failure, but because `decimal` genuinely cannot hold the intermediate range the standard permits.

**How to build Arm C — read this before designing it.** [sampleApproaches.md §2.2](sampleApproaches.md) sells the intermediate C on legibility: "flattened control flow," "LLMs are saturated with C." That is wrong — the compiler emits libcob runtime IR that no model has trained on. But the approach works anyway, for a better reason: **the compiler resolves semantics the COBOL source leaves ambiguous.**

```c
static cob_field f_22 = {3, b_17 + 6, &a_5};  /* WS-HOURS-PACKED */
a_5 = {0x12, 4, 1, 0x0001}   /* packed decimal, 4 digits, scale 1, signed */
```

A complete `REDEFINES` overlay resolution — name, offset, length, type, scale, sign. Likewise `PERFORM 0100-GROSS THRU 0300-NET` becomes an explicit `perform_through` range with labelled paragraphs and an explicit implicit-return.

So **Arm C injects extracted facts, not the raw C file**. The field table, the resolved control flow, the `libcob` call list. Pasting the whole generated file wastes context on `cob_decimal` plumbing.

This matters beyond the demo: it is the reason the approach unblocked a human expert who was stuck. The compiler answers *"what does this actually do?"* — the question that defines a migration frontier.

**Scope discipline:** one program, three arms. Do not add a second COBOL program until the first proves the harness.

**The meta-lesson worth teaching.** This approach was documented with a plausible, confident, wrong explanation; a first review over-corrected and nearly discarded a technique that works. Both errors were only settled by running the compiler. That is the lesson for the whole curriculum: **claims about grounding must be executed, not argued** — by the author and by the reviewer.

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
- The intermediate-C **legibility** claim in [sampleApproaches.md §2.2](sampleApproaches.md) is wrong, but the approach is sound for a different reason (§2.2.1). Present the corrected mechanism, not the original one.
- The external sources in [sampleApproaches.md §4.4](sampleApproaches.md) are unverified pointers. Fact-check anything before it appears in a lesson.
