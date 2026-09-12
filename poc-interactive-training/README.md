# POC Interactive Training

A runnable, 40-scene AI-governance training platform: an ASP.NET Core Blazor server, a VS Code model bridge, and a set of deterministic controls that run with no model at all. Scene 1 is the programme overview; the other 39 are working demonstrations.

The organising principle throughout: **prerequisite and assurance controls are deterministic; models supply judgement and narrative only.** A gate that depends on a model is not a gate. Every deterministic service has a startup self-check that prints its result to the console, so a broken control is visible before anyone demonstrates it.

Scene 1 is a **programme overview** that maps the six Director of AI priorities onto the demonstrations, links every other scene, and lists the eight capabilities the role calls for that this lab does **not** yet cover. It opens on the gaps deliberately.

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
| Regression audit | Three independent techniques, none of which is sufficient alone. **Differential comparison**: the production baseline and the QA candidate are both executed over every row of condition-permutation data and their responses compared; a difference matching a signed-off business rule is intended, a difference matching none is a regression. **Condition coverage**: Roslyn extracts the branch predicates, a compiled probe evaluates each against the real data, and a predicate driven only one way is a gap in the *data*. **Mutation testing**: compiles the fixture, executes it, injects one defect at a time, re-runs the tests — a surviving mutant is a defect the suite would ship |
| Context audit | ML.NET classifier over real embeddinggemma-300m vectors, rating whether a repo carries usable context |
| Portfolio context tiers | Weighted artifact scoring across an AMP/repo fixture, with tier-conflict and staleness-cascade detection |
| Common code audit | embeddinggemma clustering over three real repositories; threshold measured with `--calibrate-corpus`, not guessed |
| Token economics | Cost per *successful outcome* rather than per token; detects when the rate card would route to the wrong model |
| Pattern library gate | Six curation checks; evidence must contain a number or the contribution is held |
| Plan-first check | Six structural checks on a plan, including "no code yet" |
| CLARA compiler | Lex → parse → bind/type-check → expression tree → CIL → JIT. Conformance suite plus a measured benchmark against hand-written C# |
| COBOL migration oracle | GnuCOBOL compiles and executes the legacy program to produce ground truth; candidate migrations are executed against it |
| Autopilot manifest contract | Diffs the 152-step walkthrough manifest against the markup at startup and checks that every one of the 40 scenes has at least one step; a missing selector disables auto-run rather than failing at the click |
| COBOL domain segmentation | Parses real IBM Enterprise COBOL with the ANTLR `Cobol85.g4` grammar, resolves copybooks in SYSLIB order, builds the call graph, CRUD matrix and DDG, builds a PDG on demand, then partitions with Leiden over a resolution sweep. Publishes parse, grammar, copybook, SQL and program-reference coverage with every run |
| Context classifier | Separates a mixed two-domain design document section by section: embeddinggemma vectors through an ML.NET model trained on the two real trading repositories, fused with a lexical arm of repository vocabulary. Uncertain sections are kept, referenced sections are kept regardless of label, and the result is scored against sealed labels and planted cross-domain traps |
| LLM shootout oracle | GnuCOBOL compiles and runs a harness lifted verbatim from the IBM Global Auto Mart sample (DCLGEN copybook + the row-formatting MOVE chain). Its output — record lengths, 21 field offsets, five screen rows and four hex dumps — is the answer key every model is scored against; a synthetic perfect answer must score 32/32 at startup |
| Local speculative decoding | Drives a local llama.cpp `llama-server` (CPU build) to run Gemma 3 4B alone, with a Gemma 3 270m draft, and with an n-gram draft on the same greedy prompt. Reports llama.cpp's own decode tokens/s, drafted/accepted counts, process memory and a SHA-256 text-identity check against the baseline. Weights and the runtime are fetched, never committed |
| Notes-to-domains classifier | Reads the in-code notes of the open BankDemo COBOL app and scores five methods (retrieval, Help-excluded retrieval, nearest-centroid, supervised ML.NET, call-graph structure-aware) against the hand-written `docs/domains` catalog. Uses each domain's *Business purpose* prose only, never the section that quotes the notes. embeddinggemma vectors, cosine, SdcaMaximumEntropy and LINK/CALL/COPY call-graph clustering; every number is a cosine or a held-out prediction |
| LLM in the Room | Push-to-talk chat with a local Gemma 4 E2B (Q4, llama.cpp CPU) grounded on the fixture's OKF explanation: whisper.cpp transcribes, the answer streams and is read aloud sentence by sentence, every answer ends with a question back, and Gemma rewrites running notes from the verbatim transcript between questions. First-word, total, tok/s and cache-hit are measured per turn against room targets. No cloud, no bridge. Design: [GemmaInTheRoomFinalDesign.md](GemmaInTheRoomFinalDesign.md) |

## Auto-run: the whole programme, unattended

The header carries an **Auto-run** control that walks every scene, operates the real controls, and narrates each step. It exists so the walkthrough does not depend on a presenter who knows which button to press.

The rule that makes it safe: **no model ever infers what the application does.** Sequence is authored data in `AutopilotManifest`; each step carries a fact pack that is the only thing narration may assert, plus a `must_not_claim` list. Clicks are real DOM events on `data-auto` attributes, so the run cannot show a path a learner could not take.

| Profile | Scope |
| --- | --- |
| Deterministic | The 125 steps that need no bridge model (121 with the default in-process execution block, see below). Narration is the fact list, read verbatim. Works offline |
| Full | All 152 steps across 40 scenes, including the 27 bridge steps that run every model stage, with Gemma 4 authoring narration |

Narration falls back **Gemma 4 → Claude Opus 4.8 → the fact list**. Claude is used only on a mechanical failure of Gemma — unavailable, timeout, empty, or unparseable — never because someone judged Gemma's prose to be worse, and every substitution is disclosed on screen. Failures are narrated rather than hidden. **Escape** aborts at any point.

Four steps execute on the host rather than over the bridge: mutation testing (scene 26) compiles and executes mutated code in-process, the local speculative-decoding scene (38) launches `llama-server`, the notes-to-domains scene (39) embeds and trains against a repository on disk, and LLM in the Room (40) launches `llama-server` and `whisper-server`. The autopilot **hard-blocks** those four steps unless `Autopilot:AllowInProcessExecution` is set. It defaults to `false`.

Not yet built: narration pre-flight caching, and the Rehearsed profile that depends on it. A Full run currently generates static narration inline. See [Gemma4AutoNarratsAllTabs.md](../Gemma4AutoNarratsAllTabs.md) for the design and its open decisions.

## What needs a model, and which one

| Scene | Flow |
| --- | --- |
| Model comparison | Three models answer closed-book, then the official Google Cloud documentation is **fetched over the network** and Claude Opus 4.8 scores the blinded answers against that retrieved text |
| Context sufficiency | Gemma 4 plans the same real ticket at three documentation tiers; deterministic sealed-criterion scoring, then Claude Opus 4.8 compares the three |
| Common code audit | Claude Opus 5 designs the consolidation from the audit; Gemma 4 implements it one module at a time from the spec alone |
| Making Gemma 4 an SME | Claude answers a crypto-execution ticket unaided, consults a Gemma grounded on a three-layer vendor documentation pack, then designs. Recall is scored against verified facts, and a per-fact trace shows which stage lost each one |
| Prompt challenge | Claude reviews the learner's prompt against a visible rubric and **streams** each decision as it is made, so a long review reads as progress rather than a hang |
| Classify Context | Claude filters the same mixed design the local classifier filtered (arm D); Gemma 4 then builds the bond settlement service from the full document, the classifier's selection and Claude's selection; every build is compiled and executed against a reference oracle, and Claude reviews the results against sealed criteria |
| Tell LLM to Focus/Ignore | The classifier's dropped set becomes a deterministic instruction prepended to the full document. Claude Opus 5 builds from the mixed document, the mixed document plus the instruction, and the filtered document; prompt and output tokens are counted and every build is executed against the oracle. The live VS Code chat version is documented on the page, not built |
| Right LLM for the job | Claude Opus 5, Claude Fable 5.1, GPT-6 Astra and Gemini 3.8 Flash receive the same COBOL-to-Java design brief and return the same fixed-shape answer. Code marks every compiler-checkable value against the GnuCOBOL oracle and counts sealed SME insights; Claude Opus 4.8 (not a contestant) then judges anonymised slots for design quality. Output tokens, seconds, substitutions and whether Java was offered sit beside the score |
| Round trip, modernize, audits, CLARA, framing, drift | Gemma authors or audits; Claude reviews or grades against sealed criteria |

## External dependencies

The server reads and fetches things outside its own directory. All are optional; each degrades visibly rather than silently.

- **embeddinggemma-300m ONNX** under `models/embeddinggemma-300m-onnx/` (~320 MB, gitignored). Absent → structural fallbacks, stated in the UI.
- **GnuCOBOL** on `PATH` for the migration oracle. Absent → that scene reports the toolchain is unavailable.
- **llama.cpp CPU build + two GGUF files** for the local speculative-decoding scene: `tools/llama-cpp/cpu/llama-server.exe` (release b10909 `llama-*-bin-win-cpu-x64.zip`, or set `LLAMA_CPP_HOME`), `gemma3/gemma-3-4b-it-Q4_K_M.gguf` (2.4 GB) and `gemma3/gemma-3-270m-it-Q8_0.gguf` (278 MB) from `unsloth/*-GGUF` on Hugging Face. All gitignored. Absent → the scene lists what is missing and disables the run buttons. Needs ~3 GB of free RAM while running.
- **Gemma 4 E2B GGUF + whisper.cpp** for LLM in the Room: `Gemma4/gemma-4-E2B-it-Q4_K_M.gguf` (3.0 GB, from `cstr/gemma4-e2b-it-GGUF`), `tools/whisper-cpp/Release/whisper-server.exe` (whisper.cpp b5130 `whisper-bin-x64.zip`) and `tools/whisper-cpp/models/ggml-base.en.bin` (141 MB, from `ggerganov/whisper.cpp`). All gitignored; the same `llama-server.exe` as above. Absent → the scene names what is missing and disables Start. Needs ~3.5 GB of free RAM while the room is running, and a browser with a microphone (Edge or Chrome, not VS Code's embedded browser).
- **BankDemo repository** for the notes-to-domains scene, at `C:\Users\tkrro\Source\BankDemo` or set `BankDemoRoot`. Uses its COBOL sources and `docs/domains` ground truth. Absent → the scene reports the path is missing and disables the run button.
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
dotnet run --project .\server\... -- --score-facts <file>    # score saved model output against the SME fact list
```

## Boundaries

- The FUL-1842 JIRA, OKF bundle and `src/` fixture are synthetic. The three trading repositories are real code but were authored for this exercise.
- In-memory state is discarded when the server stops.
- Claude and Gemma output is coaching commentary or a model's opinion — never a validated grade.
- Keyword coverage on the comparison tab counts vocabulary, not correctness.
- Cost figures in the token-economics scene are **illustrative rates**, not contract pricing.
- Mutation testing compiles and executes fixture code in-process. That is acceptable on a developer machine and is not safe on a shared host without sandboxing.
- **Roslyn is a static analyser and never runs the code.** It can name every branch predicate but cannot say which the data reaches, whether a passing test asserted anything, or whether a candidate still agrees with production. Everything beyond structure on the regression tab comes from execution, not analysis.
- **A surviving mutant is a candidate gap, not a proven one.** The tool cannot detect equivalent mutants — a mutation that produces a genuinely identical program will always survive, and no test could kill it.
- **"Adjudicate" is a question, not a verdict.** When a signed-off rule applies to an input but behaviour did not change, that may be a missing implementation *or* a higher-precedence rule legitimately suppressing it. The harness deliberately reports both as needing a human decision rather than guessing.
- **Intended-change rules are C# expressions over the entry-point parameters.** A rule that depends on internal state will not compile in isolation and is reported as skipped rather than silently passing.
- **Fault injection is described on the regression tab and not executed.** Condition permutations establish that the logic still agrees with production; malformed payloads, truncated fields, duplicate messages, downstream timeouts and partial failures are a separate discipline. A suite can be strong at the first and blind to the second.
- Similarity thresholds are properties of the corpus they were measured on. Re-measure before pointing a control at a different estate.
- The streamed review trace is the reviewer narrating its own steps. It is **not** private chain-of-thought, and it is not evidence the review is correct.
- Autopilot narration is model-authored prose over verified facts. The facts are checked; the phrasing is constrained but not verified. Read the generated pack before showing it to an external audience.
- Static autopilot narration describes what a step is **designed** to do, not what was just observed. Only result narration reflects the actual outcome.
- The crypto SME grounding pack is a dated snapshot of vendor documentation. A stale pack grounds the model in confident, wrong detail — worse than no pack.
- Keyword recall scoring measures vocabulary, not correctness. Treat it as a floor.
- The autopilot itself fires 27 bridge steps on a Full run, several of which fan out to multiple models (four contestants plus a judge in the shootout, three builds plus a review in each of the classify and focus scenes) — well over 60 unattended model calls. That makes it an agent under this programme's own definition. It has no registry entry yet.
- Benchmarks printed from a Debug build are marked as such; re-run in Release before quoting a number.
- The fixture under `training-fixture/` is the canonical synthetic context for the FUL-1842 lesson; duplicated prose in application code is not authoritative.
- LLM in the Room keeps the transcript in server memory only and posts microphone audio to loopback, where it is transcribed and discarded. The running notes are model-generated and rendered with `verified: null`; the transcript, not the notes, is the record. whisper base.en is English-only and degrades on cross-talk and accents.