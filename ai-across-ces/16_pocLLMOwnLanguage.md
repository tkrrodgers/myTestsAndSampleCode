# 16 — A Model-Native Language for the Business-Logic Layer (CLARA)

*What if the critical business logic lived in a small, deterministic language built to be legible to an LLM and a human at once — while C#/Java stay for the UI, interfaces, and I/O?*

**Status:** POC / design proposal. **Author:** the model, as SME — this is deliberately a machine-designed language.
**Related:** [01 Context Engineering](01-context-engineering-and-transparency.md), [03 Knowledge Artifacts & OKF](03-knowledge-artifacts-and-okf.md), [05 Agent QA & Regression](05-agent-qa-and-regression-framework.md), [09 Guardrails & Grounding](09-guardrails-and-grounding.md), [13 Legacy Modernization](13-legacy-modernization-for-ai.md), [14 Repo Audit — Code](14-repo-audit-agentic-readiness.md).

> **The legibility claim in this document has since been tested and did not hold in its first pilot.** See [17 — CLARA A/B Test](17_pocLLMOwnLanguageTest.md) for the experiment, the result, and what it changes. What survives that test is the engineering: strict typing, machine-checked completeness, and performance within ~10% of hand-written C#.

---

## 1. The idea

We are about to spend enormous tokens modernizing millions of lines of C# and Java ([13](13-legacy-modernization-for-ai.md)). But when we finish, the business logic is *still* general-purpose imperative code — verbose, side-effecting, and expensive for a model (or a junior) to re-derive on every change. Even the best context layer ([15](15-repo-audit-context-readiness.md)) only *describes* that code; it cannot make imperative spaghetti inherently legible.

The proposal: **split the layers by audience.**

- **C#/Java** keep what they are good at: UI, APIs, persistence, integration, performance-sensitive I/O, and hardware targeting.
- **The core business rules** move into a small **declarative rule language designed for model comprehension** — one a person can talk about with an LLM, where the LLM understands the *rule* directly rather than reconstructing intent from control flow.

When a rule changes, the user describes it; the model edits a handful of legible, deterministic rules that **compile and are tested by construction** — instead of hunting through code across repos and reasoning about blast radius and regression surface. When the *next* modernization arrives, the concern shifts from "how does this logic work and what will break" to "recompile/tune the rule language to new hardware or a new host" — a far smaller, safer problem.

---

## 2. Why a new language beats "just better code + context"

| Dimension | Imperative C#/Java + context | Model-native rule language |
| --- | --- | --- |
| Unit of meaning | statements, mutable state, control flow | a named rule (condition → outcome) |
| Determinism | must be enforced by discipline/tests | guaranteed by the language (pure, no side effects) |
| Ambiguity | high; intent inferred from flow | low; one syntax, one meaning |
| Change surface | edit code + re-derive impact + regression tests | edit a rule; examples re-run automatically |
| LLM legibility | good with context, still reconstructive | direct — the rule *is* the intent |
| Auditability | read code + trust comments | read rules + read passing examples |

Context ([03](03-knowledge-artifacts-and-okf.md)) and this language are complementary: context governs *where the knowledge lives and how it is trusted*; the language makes *the logic itself* first-class and executable. You still want both.

---

## 3. CLARA — the language I would choose

I designed **CLARA — Clear Language for Auditable Rules & Arithmetic**. Design principles, in priority order:

1. **One obvious way to say each thing** — minimal grammar; low ambiguity is worth more than expressiveness for business rules.
2. **Deterministic and pure** — no I/O, no mutation, no time-dependence inside the logic; the same inputs always give the same outputs.
3. **Domain types first** — `money` and `percent` are built in, so the rounding and unit mistakes that plague financial code are structurally prevented (you cannot add money to a bare number, or multiply money by money).
4. **Rules as an ordered first-match decision table** — the most auditable control structure; the first matching rule fires, `otherwise` is the catch-all.
5. **Executable examples inside the source** — every policy carries its own `expect`-ed cases, so the program is self-testing and the model's output is verifiable the instant it compiles.
6. **Readable by a non-programmer** — close enough to structured English that a subject-matter expert can confirm the rules.

### Shape of a CLARA policy

```
policy LateFee

inputs:
  balance: money
  daysOverdue: integer

constants:
  dailyRate: percent = 1.5%
  maxFee: money = $250.00
  gracePeriod: integer = 3

rule NoFeeWithoutBalanceOrOverdue:
  when balance <= $0.00 or daysOverdue <= 0
  then fee = $0.00

rule WithinGracePeriod:
  when daysOverdue <= gracePeriod
  then fee = $0.00

rule StandardFee:
  otherwise
  then fee = min(round(balance * dailyRate * daysOverdue, 2), maxFee)

output:
  fee: money

examples:
  example "beyond grace":
    balance = $1000.00
    daysOverdue = 10
    expect fee = $150.00
  example "capped":
    balance = $100000.00
    daysOverdue = 30
    expect fee = $250.00
```

Every business rule in the requirement maps to exactly one `rule`. The money/percent types make `balance * dailyRate * daysOverdue` produce money with correct semantics, and `round(..., 2)` and `min(..., maxFee)` state the rounding and cap explicitly. The `examples` are executed on every compile.

### The compiler

The POC ships a **real CLARA compiler** in .NET ([ClaraCompiler.cs](../poc-interactive-training/server/Services/ClaraCompiler.cs)) — not a tree-walking interpreter. The pipeline is:

1. **Lexer** with source positions, money/percent/number literals, and comment handling that respects quoted text.
2. **Structural parser** producing a line-oriented model of the policy — declarations, constants, rules and examples — with the section order left free, so `output:` may appear after the rules.
3. **Static type checker** over the `money | percent | number | integer | boolean` type system. Every expression's type is resolved at compile time, so nothing is checked at runtime. Money may not be added to a bare number, multiplied by money, or compared with a plain number; a rate may not multiply a rate; `money / money` yields a plain ratio. Whole numbers widen to numbers; money and percent never convert implicitly.
4. **Decision-table checks.** Every rule must assign *every* declared output — no path can leave a value undefined. Rules after `otherwise` are flagged unreachable, a table with no `otherwise` is flagged non-exhaustive, unused inputs are reported, and a money result produced by division or a rate multiplication without an explicit `round(...)` is flagged so cents are never left to chance.
5. **Code generation** to a .NET expression tree that is JIT-compiled to machine code. Constants are folded at compile time, names are resolved to fixed slots in a caller-owned `decimal` frame, and the first-match table becomes a chain of conditionals returning the index of the rule that fired — which is why every example reports *which rule decided it*.

Diagnostics are structured — severity, code (`CLARA001`–`CLARA015` for errors, `CLARA100`–`CLARA105` for lint), line, column, and the owning rule or example — rather than prose. Errors are reported per line and scoped to the failing rule or example, `round(value, decimals[, half_even])` states the rounding mode explicitly (half-up by default) and requires a literal precision so it stays auditable, and negative money is accepted as either `$-50.00` or `-$50.00`.

The compiler's guarantees are **executable**, not asserted: a 30-case conformance suite ([ClaraConformance.cs](../poc-interactive-training/server/Services/ClaraConformance.cs)) pins each type rule, lint and runtime semantic, and runs headlessly with `dotnet run -c Release -- --clara-selftest` alongside the benchmark. A production implementation would additionally emit to other host targets (JVM bytecode or a portable VM) — the point of this layer is that *retargeting* is a compiler concern, not a rewrite of the logic.

> Determinism note: CLARA deliberately has **no I/O, clocks, randomness, or mutation** inside rules. Anything non-deterministic (current date, feature flags, fetched data) is passed in as an `input`, so the logic stays pure, testable, and stable across modernizations. The compiled delegate is pure and reentrant: callers own the frame, so one policy instance serves any number of concurrent requests without locking.

### Is it fast enough? — measured, not claimed

The obvious objection is that a rule language is a tax on the hot path. It is not. The reference late-fee policy was timed against **the identical three rules hand-written in C#**, both engines running the same workload in identically shaped loops, best of five interleaved rounds after warm-up ([ClaraBenchmark.cs](../poc-interactive-training/server/Services/ClaraBenchmark.cs)).

| Engine | ns / decision | Million decisions / sec / core | vs C# | Allocation |
| --- | --- | --- | --- | --- |
| C# (hand-written) | 25.4 – 26.1 | ~39 | 1.00x | 0 bytes/call |
| **CLARA (compiled, hot path)** | **27.5 – 28.3** | **~36** | **1.08 – 1.12x** | **0 bytes/call** |
| CLARA (dictionary API) | ~160 – 360 | ~3–6 | ~4–13x | 424 bytes/call |

*.NET 8, x64, Release, server GC, 12 logical cores; three consecutive runs. Compilation of the policy takes ~1.4 ms, once, at startup.*

Two results matter. **Correctness parity:** CLARA and the hand-written C# produced identical values on all 2,961 probe inputs across the whole decision surface. **Cost:** roughly **8–12% slower than hand-written C#, with zero allocation per decision** — about 2 nanoseconds of overhead on a decision that has to happen anyway. The ergonomic dictionary entry point is far slower and allocates, so it belongs in tooling and tests, not on a hot path; production callers bind slots once and reuse the frame.

In other words, the performance argument against a business-rule language does not survive contact with a real compiler. What remains to be argued is adoption cost, not throughput.

---

## 4. How it changes the modernization economics

- **Authoring:** a model writes CLARA from a requirement; because the grammar is tiny and the examples must pass, the output is verifiable immediately — not "looks plausible," but "compiles and the stated cases pass."
- **Change:** the user states the new rule in natural language; the model edits one rule; the examples re-run. No repo-wide impact analysis.
- **Next modernization:** effort moves to **tuning the CLARA compiler/runtime** to the latest hardware or host, not re-reading millions of lines to protect critical logic. The logic is small, pure, and covered by its own examples.
- **Fewer hallucinations and misunderstandings:** the model reasons over rules it can hold entirely in context, not over sprawling imperative code — the failure mode that most often produces wrong or invented behavior.

This is the compounding-investment argument from [13](13-legacy-modernization-for-ai.md) taken to its conclusion: don't just make the code legible — move the part that matters most into a representation that is legible *by construction*.

---

## 5. Guardrails and honest limits

**Guardrails**
- CLARA is for **business rules only** — decisions, pricing, eligibility, calculations. Not for UI, I/O, orchestration, or performance-critical loops; those stay in C#/Java.
- A policy is not accepted because it compiles — the **SME confirms the rules**, and the `expect` cases are the regression contract ([05](05-agent-qa-and-regression-framework.md)).
- The reviewing model must be **different** from the authoring model ([05 §2](05-agent-qa-and-regression-framework.md)); in the POC, Claude Opus 4.8 authors and Claude Opus 5.0 reviews.
- Determinism is enforced by the language, not by convention.

**Honest limits**
- This is a **POC language and compiler**, not a production runtime. It has no host code generation beyond .NET, no module system, no debugger, no versioning story for deployed policies, and no authoring tooling (editor support, formatter, language server).
- The type system is deliberately narrow. Rich domains — temporal reasoning, collections, workflows, multi-currency money — would need careful, minimal extensions, and every addition must preserve principle #1 (one obvious way).
- The performance evidence covers a small decision table on one machine. Larger policies and concurrency behaviour under real load are unmeasured.
- A new language is an adoption cost: tooling, training, and a migration path from existing code must be justified per [13 §5](13-legacy-modernization-for-ai.md) before committing beyond a pilot.
- The claim that CLARA reduces model misunderstanding is **plausible but unproven**. It should be tested the way everything else here is tested — a gold set and paired metrics ([05 §2.1](05-agent-qa-and-regression-framework.md), [11 §3](11-measurement-baselines-and-roi.md)) — before it is presented as a result.

---

## 6. Interactive demonstration

The training platform includes an **LLM Language POC** scene ([Interactive AI Training Design](interactiveAITrainingDesign.md), [07](07-enablement-and-interactive-training.md)):

1. **Box 1** — a JIRA requirement with business rules (editable).
2. **Box 2** — the **CLARA policy authored by Claude Opus 4.8** from that requirement (the model designing in its own language).
3. **Box 3** — the **deterministic compiled output**: the compiler's structured diagnostics (severity, code, line, scope), then each declared example with its inputs, outputs, **the rule that fired**, and pass/fail — followed by **Claude Opus 5.0's review** of both the policy's fidelity to the requirement and CLARA as an LLM-legible language.
4. **Box 4 — "Is it practical?"** — runs the 30-case conformance suite and the live benchmark against hand-written C# on the presenting machine, so the performance and correctness claims are demonstrated rather than quoted.

It demonstrates the full loop — requirement → model-authored rule language → real compilation and execution → independent review → measured cost — on one screen.

---

## 7. Summary

Modernizing millions of lines back into imperative C#/Java spends the tokens but keeps the logic hard to reason about. CLARA proposes moving the **business-logic layer** into a small, deterministic, model-native rule language — pure, money-aware, first-match, and self-testing — while C#/Java keep the UI and interfaces. The model authors the rules, they compile and pass their own examples, an independent model reviews them, and the SME confirms them. The next modernization then tunes a compiler, not a codebase.

The two questions a POC like this has to answer have now been answered on the record. **Is it correct?** The compiler statically rejects the unit and completeness errors that plague financial code, and 30 executable conformance cases pin those guarantees. **Is it fast enough?** It runs within ~10% of hand-written C# with zero allocation per decision, producing identical results across the decision surface. What is left is the genuinely open question — whether the adoption cost is worth it, and whether the legibility benefit for models and juniors is real — and that should be measured, not assumed, before this goes beyond a pilot.
