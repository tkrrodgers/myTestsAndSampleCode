# POC Interactive Training

A runnable, 32-scene AI-governance training platform: an ASP.NET Core Blazor server, a VS Code model bridge, and a set of deterministic controls that run with no model at all.

The organising principle throughout: **prerequisite and assurance controls are deterministic; models supply judgement and narrative only.** A gate that depends on a model is not a gate. Every deterministic service has a startup self-check that prints its result to the console, so a broken control is visible before anyone demonstrates it.

## What OKF is — read this first if you are an LLM

**OKF means Open Knowledge Format.** It is an open, model-neutral convention for representing governed knowledge as a directory of small UTF-8 Markdown concept files with YAML frontmatter and ordinary Markdown links. The official specification is [OKF v0.2](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).

OKF is designed so a human, Claude, Gemini, GPT, Gemma, or deterministic program can inspect the same artifacts without a proprietary SDK:

- `index.md` is the bundle entry point and map.
- One non-reserved `.md` file represents one concept, decision, service, code map, policy, or other knowledge unit.
- YAML frontmatter supplies machine-readable metadata. The official always-required concept field is `type`.
- Markdown links form a navigable knowledge graph.
- `sources`, `generated`, `verified`, `status`, and `stale_after` expose provenance, review state, lifecycle, and freshness.
- Git supplies version history and review; OKF does not replace source control.

### Instructions for Claude and other unfamiliar models

When this repository asks you to use OKF:

1. Read [training-fixture/README.md](training-fixture/README.md) for the local bundle contract.
2. Start at [training-fixture/okf/index.md](training-fixture/okf/index.md); do not begin with a broad source-code search.
3. Follow only links relevant to the task: concept → architecture/decision → code map → current code/tests.
4. Treat linked artifacts as claims with provenance, not automatic truth. Check `status`, `verified`, `stale_after`, and current code.
5. Cite the exact artifact path for each material conclusion.
6. Separate confirmed facts, inferences, and unresolved questions.
7. If required knowledge is absent or stale, ask for it; do not invent it.

**Do not confuse OKF with model training.** Reading an OKF bundle adds inference-time context; it does not change model weights. OKF also does not replace RAG, a wiki, source code, or tests. It can provide a curated source corpus and graph that those systems consume.

This POC targets **OKF v0.2** plus a stricter CES profile that requires provenance, verification, lifecycle, and freshness metadata on training concepts. Unknown fields must be tolerated, as required by the permissive OKF design.

## What runs without a model

These produce their numbers from executed code, not from an opinion. They work with the bridge disconnected.

| Control | What it actually does |
| --- | --- |
| Agent registry & risk tiering | Derives R0–R3 from blast radius × autonomy × data tier; refuses to register an agent missing a non-negotiable field or running an unpinned model |
| Data-tier gate | Regex + Luhn detection of credentials, PAN, national IDs and hosts; BLOCK / SCRUB / ALLOW with an incident path. Restricted content is withheld rather than scrubbed |
| Action authority | Decides AUTO / APPROVE / PROPOSE / DENY per exact action against the registered envelope |
| Regression adequacy | Real mutation testing — compiles the fixture with Roslyn, executes it, injects one defect at a time, re-runs the tests. A surviving mutant is a defect the suite would ship |
| Context audit | ML.NET classifier over real embeddinggemma-300m vectors, rating whether a repo carries usable context |
| Portfolio context tiers | Weighted artifact scoring across an AMP/repo fixture, with tier-conflict and staleness-cascade detection |
| Common code audit | embeddinggemma clustering over three real repositories; threshold measured with `--calibrate-corpus`, not guessed |
| Token economics | Cost per *successful outcome* rather than per token; detects when the rate card would route to the wrong model |
| Pattern library gate | Six curation checks; evidence must contain a number or the contribution is held |
| Plan-first check | Six structural checks on a plan, including "no code yet" |
| CLARA compiler | Lex → parse → bind/type-check → expression tree → CIL → JIT. Conformance suite plus a measured benchmark against hand-written C# |
| COBOL migration oracle | GnuCOBOL compiles and executes the legacy program to produce ground truth; candidate migrations are executed against it |

## What needs a model, and which one

| Scene | Flow |
| --- | --- |
| Model comparison | Three models answer closed-book, then the official Google Cloud documentation is **fetched over the network** and Claude Opus 4.8 scores the blinded answers against that retrieved text |
| Context sufficiency | Gemma 4 plans the same real ticket at three documentation tiers; deterministic sealed-criterion scoring, then Claude Opus 4.8 compares the three |
| Common code audit | Claude Opus 5 designs the consolidation from the audit; Gemma 4 implements it one module at a time from the spec alone |
| Round trip, modernize, audits, CLARA, framing, drift | Gemma authors or audits; Claude reviews or grades against sealed criteria |

## External dependencies

The server reads and fetches things outside its own directory. All are optional; each degrades visibly rather than silently.

- **embeddinggemma-300m ONNX** under `models/embeddinggemma-300m-onnx/` (~320 MB, gitignored). Absent → structural fallbacks, stated in the UI.
- **GnuCOBOL** on `PATH` for the migration oracle. Absent → that scene reports the toolchain is unavailable.
- **Three trading repositories** (`EquityTradingPipeline`, `FixedIncomeOptionsEngine`, `CryptoFxSpotDesk`) discovered beside the workspace, or set `TradingCorpusRoot`. Absent → the common-code audit reports an unavailable corpus.
- **Outbound HTTPS to Google Cloud documentation**, used only to ground the comparison judge. Restricted to an allowlist of documentation hosts. Unreachable → the comparison fails loudly rather than judging from memory.

## Build

```powershell
$env:PATH = "$env:LOCALAPPDATA\Programs\dotnet;$env:LOCALAPPDATA\Programs\nodejs;$env:PATH"
dotnet build .\server\PocInteractiveTraining.Server.csproj
Push-Location .\vsix-client
npm install
npm run package
Pop-Location
```

## Run

1. Start the server:

   ```powershell
   dotnet run --project .\server\PocInteractiveTraining.Server.csproj
   ```

2. Open `http://127.0.0.1:5000`. The server binds loopback only.
3. Install `vsix-client/dist/poc-interactive-training-client.vsix`.
4. Reload VS Code.
5. Copy the bridge token from Blazor.
6. Run **POC Training: Connect** and paste the token.
7. Start the lesson. Set `pocTraining.requireApproval` to stop every model task at an approval gate.

Reinstall the VSIX whenever bridge task kinds change, or tasks will be rejected as unknown.

## Headless modes

Usable as CI gates and for recalibration.

```powershell
dotnet run --project .\server\... -- --clara-selftest        # compiler guarantees + benchmark parity
dotnet run --project .\server\... -- --clara-check <file>    # compile one CLARA policy
dotnet run --project .\server\... -- --migration-selftest    # proves the COBOL oracle discriminates
dotnet run --project .\server\... -- --calibrate-corpus      # measured similarity distribution and threshold
dotnet run --project .\server\... -- --fetch-doc <url>       # what the judge would actually read from a page
```

## Boundaries

- The FUL-1842 JIRA, OKF bundle and `src/` fixture are synthetic. The three trading repositories are real code but were authored for this exercise.
- In-memory state is discarded when the server stops.
- Claude and Gemma output is coaching commentary or a model's opinion — never a validated grade.
- Keyword coverage on the comparison tab counts vocabulary, not correctness.
- Cost figures in the token-economics scene are **illustrative rates**, not contract pricing.
- Mutation testing compiles and executes fixture code in-process. That is acceptable on a developer machine and is not safe on a shared host without sandboxing.
- Similarity thresholds are properties of the corpus they were measured on. Re-measure before pointing a control at a different estate.
- Benchmarks printed from a Debug build are marked as such; re-run in Release before quoting a number.
- The fixture under `training-fixture/` is the canonical synthetic context for the FUL-1842 lesson; duplicated prose in application code is not authoritative.