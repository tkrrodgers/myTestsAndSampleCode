# 17 — Does the Representation Change What an Agent Gets Right? (CLARA A/B Test)

*An agent starts with no knowledge of your business. Does encoding the business logic in a model-native language with its local knowledge attached change what the agent produces — or is it just a different way to write the same thing?*

**Status:** POC experiment design + first pilot result (n=1). **Result so far: the hypothesis did not hold.**
**Related:** [16 — CLARA](16_pocLLMOwnLanguage.md), [01 Context Engineering](01-context-engineering-and-transparency.md), [03 Knowledge Artifacts & OKF](03-knowledge-artifacts-and-okf.md), [05 Agent QA & Regression](05-agent-qa-and-regression-framework.md), [11 Measurement & Baselines](11-measurement-baselines-and-roi.md), [15 Repo Audit — Context](15-repo-audit-context-readiness.md).

---

## 1. The problem this tests

A model has seen billions of lines of C# and Java. It has seen **none** of your business. Every team compensating for that gap is doing the same thing: injecting context — into a chat window that runs out, or into artifact files that must be found, maintained, and paid for in tokens on every request ([03](03-knowledge-artifacts-and-okf.md), [15](15-repo-audit-context-readiness.md)).

The uncomfortable reality behind that effort is that **many agents run with far less context than their authors assume**. Users hand an agent a JIRA ticket and a repository and expect an implementation. The agent gets the code, and the code contains the rules but not the reasons.

So the question is not "is CLARA a nice language." It is:

> When an agent is given a ticket and **no context at all**, does it do better against business logic written in a representation that carries its own local knowledge?

Three goals were set for this, and they are tested separately because they can fail separately:

| Goal | How it is tested | Verdict so far |
| --- | --- | --- |
| **G1 — Correctness under no context.** The agent produces a change that respects local rules it was never told. | Sealed criteria graded per arm | **Not met.** C# arm 8/8, CLARA arm 7/8 |
| **G2 — Token efficiency.** The representation costs less context than code plus the artifacts needed to explain it. | Real Gemma tokenizer counts | **Not met at this scale.** CLARA costs ~2x C#-plus-documents |
| **G3 — Speed.** No throughput is given up. | Compiled benchmark vs hand-written C# | **Met.** 1.09–1.11x, zero allocation ([16](16_pocLLMOwnLanguage.md)) |

---

## 2. What was added to CLARA to make this testable

CLARA as described in [16](16_pocLLMOwnLanguage.md) was a decision table with types. It could express the *rules* but not the *reasons*, which is exactly the knowledge a model lacks. Five constructs were added, all compile-time or verification-time so the hot path is untouched:

| Construct | What local knowledge it carries | Enforcement |
| --- | --- | --- |
| **Descriptions** — a quoted line on the policy, and on every input, constant, derived stage and output | The glossary. What `closureDaysLate` means *here*; where the 4.5% rate comes from | Lint `CLARA106` when missing |
| **`because "..."`** on every rule | The authority. Which policy section, statute or ADR makes this rule true — and therefore whether it is safe to change | Lint `CLARA107` when missing |
| **`requires:`** | The domain the caller must guarantee | Type-checked; violation reported per example |
| **`invariants:`** | The system-wide guarantee that must survive someone editing one rule | Type-checked; evaluated after every example; may read outputs |
| **`derive <name>: <type>`** | Named, documented, cited intermediate stages, so multi-step logic has vocabulary instead of local variables | Own first-match table; must be exhaustive (`CLARA017`); may only read stages declared above it |

The compiler enforces the rest as before: strict money/percent typing, every rule assigns every value it owns, ordered first-match tables, explicit rounding. Conformance is now **40 executable cases** (`dotnet run -c Release -- --clara-selftest`), and a single policy can be checked in CI with `--clara-check <file>`.

Measured cost of all of this at runtime: **none**. Preconditions and invariants are compiled into separate delegates and are not evaluated by `Execute`; descriptions and citations vanish at compile time. The benchmark is unchanged at **1.09x hand-written C#, 0 bytes allocated per decision**.

---

## 3. Experiment design

**Fixture.** A depot late-return billing calculation, written twice with identical behaviour: an ordinary C# class ([LanguageTestSamples.CSharpSource](../poc-interactive-training/server/Services/LanguageTestSamples.cs)) where the local knowledge exists only as bare constants (`Cap = 0.80m`, `Floor = 15.00m`, `Grace = 2`), and a CLARA policy carrying the same rules with descriptions, `because` citations, preconditions and invariants.

**Ticket.** `RENT-4471` — add a 10% loyalty discount for customers with 5 or more prior rentals. Deliberately simple to state and dependent on knowledge neither arm holds: *where in the adjustment order* the discount belongs.

**Arms.** The same model (Gemma 4 31B) receives the same ticket, one arm over C#, one over CLARA. **Neither arm receives the policy documents.** The CLARA arm additionally receives a language primer, because no model has pretraining on CLARA; that cost is counted against it.

**Grading.** Eight sealed criteria, authored from the withheld ground truth, revealed only after grading. Claude Opus 5.0 grades every criterion for every arm and must quote evidence. The CLARA arm's answer is additionally compiled and executed by the server.

**What is deliberately withheld from both arms:** OPS-POL-31 §§2,3,4,7,9; State tariff §12 (the statutory $15.00 floor); ADR-031 (adjustments are applied in a fixed order, and any new adjustment is discretionary and therefore belongs *before* the ceiling and the floor).

The whole design lives in scene 15 of the training platform, **POC Language test** — five boxes (C# source, CLARA source, the ticket, each arm's answer) plus the graded review.

---

## 4. Pilot result (n = 1)

### 4.1 What each arm did

**C# arm** inserted one line next to the existing discretionary reduction:

```csharp
if (damageWaiver) { fee *= 0.5m; }
if (completedRentals >= 5) { fee *= 0.9m; }   // added
var ceiling = replacementValue * Cap;
```

**CLARA arm** added an input, a cited constant, and a **new derived stage placed after `cappedFee`** — that is, after the insurance ceiling:

```
derive discountedFee: money "charge after loyalty discount is applied"
  rule LoyaltyDiscount:
    when previousRentals >= 5
    then discountedFee = round(cappedFee * (100% - loyaltyDiscount), 2)
    because "RENT-4471: repeat customers receive a loyalty discount"
```

It also added a matching example, updated all six existing examples with the new input, and preserved every description, citation and invariant.

### 4.2 Grades

| Criterion | C# arm | CLARA arm |
| --- | --- | --- |
| 1. Discount only at ≥5 prior rentals | yes | yes |
| 2. Unbillable return stays $0.00 | yes | yes |
| 3. **Discount applied before the insurance ceiling** | **yes** | **no** |
| 4. $15.00 statutory minimum holds after the discount | yes | yes |
| 5. Rounded 2dp half-up after the discount | yes | yes |
| 6. Closure days and contract grace unchanged | yes | yes |
| 7. Waiver halving still before the ceiling | yes | yes |
| 8. $15.00 treated as a statutory floor, not a tunable constant | yes | yes |
| | **8/8** | **7/8** |

**The C# arm won.** On a $1,000 asset returned 30 days late by a loyal walk-up customer, the C# arm bills $800.00 (correct); the CLARA arm bills $720.00.

### 4.3 The finding that matters more than the score

The CLARA arm's wrong answer **compiled cleanly and passed 8 of 8 examples with zero warnings.** Its own added example — $1,000, 10 days late, 5 prior rentals — is a case where the ceiling does not bind, so both orderings produce $405.00. The example suite could not tell the two orderings apart, and none of the invariants encoded ordering.

*A verified-wrong answer is more dangerous than an unverified-right one, because the green badge suppresses review.* If CLARA is going to be sold on machine verification, the verification has to be strong enough to catch this class of error. Here it was not.

The review also found a genuine defect in the **reference policy itself**: the invariant `fee <= round(replacementValue * insuranceCeiling, 2)` contradicts the statutory floor whenever the asset is worth less than $18.75. No example exposed it. It has been corrected to `fee <= max(round(replacementValue * insuranceCeiling, 2), adminFee)` and two discriminating examples were added. That correction was produced *by the invariant mechanism being written down at all* — the contradiction is not expressible, and therefore not findable, in the C# version.

### 4.4 Token cost (real Gemma tokenizer)

| Artifact | Tokens |
| --- | --- |
| C# class alone | 394 |
| C# class + the policy documents it would need to be understood | ~840 |
| CLARA policy — rules, glossary, citations, preconditions, invariants and 8 executable examples | 1,700 |

**G2 is not met.** For one policy, CLARA costs roughly twice C# plus its explaining documents, and the CLARA arm's prompt additionally carries a language primer the C# arm does not need. The counter-arguments are real but unproven: the CLARA figure includes a test suite the C# class does not have, the primer is fixed overhead amortised across every policy in a codebase, and artifact bundles grow with the repository while a policy does not. None of that is measured yet, and it should not be claimed until it is.

---

## 5. Why the CLARA arm failed, and what it implies

The representation plausibly **caused** the failure rather than merely failing to prevent it. In C#, "add an adjustment" means "add a line," and the natural insertion point is beside the existing reduction — which happens to be correct. In CLARA, "add an adjustment" means "add a stage," and stages read as an append-only pipeline, so the new stage landed at the end, past the ceiling. Named-stage decomposition made the ordering constraint *less* visually salient, because the discount and the ceiling were no longer adjacent lines.

Note also that the C# arm did not *reason* its way to the right answer. It appended next to the other reduction because that is where a linear procedure invites an edit. **Correct output, unearned.** The same luck will not hold on a ticket whose correct insertion point is not adjacent.

Two consequences follow, and they point in opposite directions:

- **Against CLARA:** stage decomposition introduces an ordering hazard that plain procedural code does not have, and it will recur on every future discretionary adjustment.
- **For CLARA:** that hazard is *checkable*. Stages are named, typed and cited, so "discretionary reductions precede the ceiling" can become a compiler rule. There is no equivalent cheap check on `fee *= 0.9m` in an untested method — and the C# arm added no test, so its 8/8 rests entirely on one reviewer reading eleven lines.

---

## 6. What this does not show

- **n = 1.** One ticket, one sample per arm, no repeats, no temperature control. A one-point gap over eight binary criteria is inside sampling noise. This is an anecdote about a method, not a measurement of a language.
- **Unweighted scoring.** Criterion 3 is a real pricing error; criteria 6 and 7 are "did not break what you were told not to touch." Counting them equally flatters whichever arm avoids cheap failures.
- **Asymmetric verification.** The CLARA arm was compiled and example-checked; the C# arm was not, and had no harness. The arms are not on the same assurance scale.
- **The grader is not blind** to which arm is which, and knew the hypothesis under test.
- **The arms' starting artifacts are not equivalent** — the CLARA source carries citations the C# source does not. That confound runs *in CLARA's favour*, and CLARA still lost.

---

## 7. The experiment that would actually settle it

1. **≥20 tickets per arm** across at least three adjustment types (insert-before, insert-after, modify-existing-rule), plus a **modify-existing** task class, which is where CLARA should be strongest.
2. **Matched source artifacts** — either both arms carry citations, or neither does. Otherwise nothing is attributable to representation.
3. **The same sealed test harness for both arms**, with a pre-registered case set that includes ceiling-binding, floor-binding and zero-billable-day boundaries. The C# arm needs a test project so both are verifiable.
4. **Blind the grader** to arm identity where the syntax permits, and anchor it with a human-labelled gold set ([05 §2.1](05-agent-qa-and-regression-framework.md)).
5. **Pre-register criteria weights** so a pricing error does not score the same as a cosmetic one.
6. **Report paired metrics** — correctness with tokens, attempts-to-correct with cost — and an evidence grade ([11 §3, §8.1](11-measurement-baselines-and-roi.md)).
7. **Ablation:** run the CLARA arm with and without the language primer, and the C# arm with and without the policy documents, to separate the pretraining gap from the representation effect.

---

## 8. Recommended changes to CLARA before the full run

1. **An ordering lint.** Let a stage declare its class — `derive discountedFee: money reduction "..."` — and let the policy state the required order once. This turns ADR-031 from prose into an enforced control, and would have caught the pilot failure at compile time. *This is the single highest-value change and it comes directly from the pilot.*
2. **Example-set adequacy checking.** Warn when no example distinguishes two adjacent stages — a rough mutation check. The pilot's false green is the failure mode to close.
3. **Invariant soundness checking.** The reference policy shipped a self-contradictory invariant that no example exposed. Even lightweight range checking over constants would have found it.
4. **Do not add anything else** until the above are proven. Every construct added to CLARA raises the in-context learning cost for exactly the small models this is meant to help.

---

## 9. Honest summary for leadership

The engineering claim in [16](16_pocLLMOwnLanguage.md) holds: CLARA compiles, is strictly typed, is machine-verified by 40 conformance cases, and runs within ~10% of hand-written C# with zero allocation.

The claim tested here — that a model-native representation carrying its own local knowledge helps an agent working without context — **did not hold in its first pilot.** The C# arm scored 8/8 and the CLARA arm 7/8, and the CLARA arm's error was masked by a passing test suite.

What the pilot did demonstrate is the **method**: a controlled A/B, sealed criteria, an independent grader, and a machine-checked arm found a real defect in a fixture a senior reviewer had already read. That is worth keeping regardless of which representation wins.

The right thing to say to leadership is: *we built the thing, we tested it honestly, it lost its first round, we know why, and we know what to fix.* Fund the 20-ticket run in §7, or stop — but do not present CLARA as proven on legibility. It is proven on performance and correctness enforcement only.
