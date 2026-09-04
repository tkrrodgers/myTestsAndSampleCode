# POC Interactive Training

Runnable OKF context-navigation lesson with an ASP.NET Core Blazor UI and a VS Code model bridge.

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

2. Open `http://localhost:5000`.
3. Install `vsix-client/dist/poc-interactive-training-client.vsix`.
4. Reload VS Code.
5. Copy the bridge token from Blazor.
6. Run **POC Training: Connect** and paste the token.
7. Start the lesson and approve each model task in VS Code.

## Boundaries

- Synthetic Public JIRA, OKF, and code fixture only.
- In-memory state is discarded when the server stops.
- Gemma authors grounded narration text; browser speech produces optional audio.
- GPT-5.6 Sol is a visible mechanical fallback for Gemma availability failures.
- Claude feedback is coaching commentary, not a validated grade.
- Every model call requires explicit confirmation in VS Code.
- The fixture under `training-fixture/` is the canonical synthetic context supplied to Gemma and Claude; duplicated prose in application code is not authoritative.