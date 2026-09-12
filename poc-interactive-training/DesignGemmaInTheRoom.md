Updated todo list

Read memory 

## Solution: "Gemma in the room" — a push-to-talk mob-elaboration assistant on this laptop

### The honest constraint first

Measured on this machine (scene 38, CPU, greedy): Gemma 3 4B Q4 decodes at **~14 tok/s alone**. A 270m draft lifts that to 18–25 tok/s **only on code/edit prompts** (74–99% acceptance); on **prose it drops to 56% acceptance and gets slower**. Mob elaboration is prose. So speculative decoding with a draft model is the wrong lever here — and a Gemma 3 draft cannot serve a Gemma 4 target anyway (different tokenizers). The Vulkan iGPU path was also slower than CPU, and llama.cpp cannot use the Ryzen AI NPU.

The speed has to come from **sending less and saying less**, not from decoding faster.

### Architecture

```mermaid
flowchart LR
    subgraph Room
        BTN[Push-to-talk button<br/>+ speaker name] --> MIC[Mic capture]
    end
    MIC --> STT[whisper.cpp server<br/>base.en, streaming while held]
    STT --> T[transcript.md<br/>verbatim · timestamped · speaker-stamped<br/>DETERMINISTIC RECORD OF TRUTH]
    STT --> A
    subgraph Lane A — foreground, latency-critical
        A[Build small prompt:<br/>system + notes digest ≤600 tok<br/>+ last 6 turns + question] --> LS[llama-server<br/>Gemma 4 E2B Q4 · CPU · n-gram draft<br/>prompt cache reuse · max 160 tok]
        LS -->|stream| UI[Screen: tokens as they arrive]
        LS -->|sentence chunks| TTS[Browser speech]
    end
    subgraph Lane B — background, runs only when Lane A idle
        T --> N[Same server, 2nd slot:<br/>update notes digest, decisions,<br/>open questions, actions]
        N --> NOTES[notes.md · OKF frontmatter<br/>generated: gemma-4-e2b · verified: null]
    end
    BTN -.->|press cancels Lane B| N
```

**The button is the design.** Pressing it does three things at once: starts capture, stamps the speaker's name on the turn (no diarization — that is too slow locally and the presser *is* the label), and cancels any background note-taking so the model is free the instant they release.

### Model choice — measure, don't guess

| Candidate | Size | Why | Expected |
| --- | --- | --- | --- |
| **`gemma4-e2b-it-Q4_K_M`** (download from the same `cstr` repo) | ~2.6 GB | CPU decode is memory-bandwidth-bound; Q4 weights move ~half the bytes of Q8. E2B has ~2B effective params, so should beat the 4B | Best bet for spoken chat |
| `gemma4-e2b-it-q8_0` (downloading now) | 4.9 GB | Slightly better quality, but likely ~half the tok/s of Q4 on CPU | Keep for the notes lane if quality matters more than speed there |
| `gemma-3-4b-it-Q4_K_M` (have it) | 2.4 GB | Known quantity: 14 tok/s | Fallback / control |
| `gemma-3-270m` | — | Too weak to answer; useless as a draft for prose | Don't use |

Run all three through the existing `spec-smoke.ps1` machinery with a **150-token prose Q&A prompt**, arms `baseline` and `ngram-mod` only. Record time-to-first-token and tok/s. Go/no-go: first token < 1.5 s, ≥ 20 tok/s.

### Latency levers, in order of impact

1. **Cap output.** System prompt: "Answer in ≤3 sentences unless asked for detail." `max_tokens 160`. At 20 tok/s that is 8 s worst case, and speech starts after the first sentence.
2. **Keep the prompt small and the prefix stable.** Fixed system prompt → rolling digest → last 6 turns → question. Never feed the whole transcript. Run `llama-server` with `--cache-reuse 256` so the unchanged prefix is not re-prefilled.
3. **Q4, not Q8**, for the foreground lane.
4. **Stream to screen; chunk TTS by sentence.** Perceived latency is time to first *sentence*, not time to last token. The browser `SpeechSynthesis` already in the app is enough.
5. **`--spec-type ngram-mod`.** Free (no second model, no memory), gave +12% on prose here, and it helps most exactly when the model quotes the notes back — which it will.
6. **CPU build, `-t 8`** (physical cores). Two slots (`-np 2`) so the notes lane can share weights; it only runs when the button is up.
7. **STT streams while the button is held** (whisper.cpp `base.en`, ~1 s per 10 s audio on CPU), so transcription finishes ~0.3 s after release.

Memory: E2B Q4 + 4k KV + whisper base ≈ 4 GB. Fits comfortably with the Blazor server.

### Latency budget (target, then measure)

| Stage | Target |
| --- | --- |
| Button release → transcript final | 0.3 s |
| Prefill (~300 new tokens, cached prefix) | ~1 s |
| First visible token | **< 1.5 s** |
| First spoken sentence | **< 3 s** |
| Full 3-sentence answer | < 8 s |

### What gets recorded — and what is trustworthy

- **`transcript.md`** — verbatim STT text, timestamp, speaker name. Deterministic. This is the record.
- **`notes.md`, `decisions.md`, `open-questions.md`, `actions.md`** — model-generated in the background lane, in OKF frontmatter with `generated: gemma-4-e2b-it-Q4`, `verified: null`, `sources: transcript.md`. The team sets `verified` at the end of the session.
- The digest the foreground lane reads is the same notes file, so the assistant's answers are grounded in what was actually said, not in what it remembers.
- The resulting plan can be pushed straight through the existing **Plan-first** deterministic check (scene 32) before the session ends.

### Governance (this is an agent)

Register it (scene 22): blast radius = sandbox, autonomy = suggests, data tier = Internal/Confidential → R1. Everything runs on loopback; nothing leaves the laptop. Run the data-tier gate (scene 27) over the transcript before any export to a share. Pin the GGUF hash and the whisper model in the release.

### Where it lands in the platform

**Scene 40 — "Gemma in the room."** Reuses `LocalInferenceService` to launch `llama-server` (already driving scene 38), adds a `whisper-server.exe` under `tools/whisper-cpp/` (gitignored, like llama.cpp), a push-to-talk component with a speaker picker, and a session folder writer. The scene's first section is the bake-off table above, so the model choice is a measured number on the page — same rule as everywhere else.

### Not recommended, and why

- **Speculative decoding with a draft model** — measured slower on prose here.
- **Cloud models** — round-trip latency plus confidential meeting content.
- **The `.litertlm` Gemma 4 E2B files in the repo** — there is an Intel LNL NPU build but no AMD one; the generic GPU build might run, but it is unmeasured and the Vulkan path was slower than CPU. Worth a 10-minute probe, not a plan.
- **AMD Ryzen AI / Lemonade NPU path** — supports some ONNX models on XDNA2; Gemma 4 support is unverified. Probe only.
- **Speaker diarization** — too slow locally; the button replaces it.

