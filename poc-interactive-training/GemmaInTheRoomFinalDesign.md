# Gemma in the Room — Final Design

*A push-to-talk assistant that sits in the room with a team: listens when someone presses the microphone, answers in a sentence or three, asks a question back, and keeps the notes — entirely on a 16 GB laptop with no cloud model and no VS Code bridge.*

**Status:** Built as scene 40, **LLM in the Room**, in `poc-interactive-training`.
**Hardware this was measured on:** Dell, AMD Ryzen AI 7, 15.2 GB RAM, Radeon 840M iGPU, no discrete GPU.
**Supersedes:** [DesignGemmaInTheRoom.md](DesignGemmaInTheRoom.md) (the solution sketch). This document records what was actually built and what was measured.

---

## 1. The constraint that shaped everything

Measured on this machine in scene 38 (CPU, greedy decoding): Gemma 3 4B Q4 decodes at **~14 tok/s alone**. A 270m draft model lifts that to 18–25 tok/s **only on code and edit prompts** (74–99 % acceptance); on **prose it falls to 56 % acceptance and gets slower**. Conversation is prose. Speculative decoding with a draft model is therefore the wrong lever — and a Gemma 3 draft cannot serve a Gemma 4 target in any case (different tokenizers). The Vulkan iGPU path was also measured slower than CPU, and llama.cpp cannot use the Ryzen AI NPU.

So the speed had to come from **sending less and saying less**, and the model choice from **measuring, not guessing**.

## 2. What was measured before building

| Candidate | On disk | Decode | Prefill | Load | Verdict |
| --- | --- | --- | --- | --- | --- |
| **Gemma 4 E2B `Q4_K_M`** (`Gemma4/gemma-4-E2B-it-Q4_K_M.gguf`) | 2,963 MB | **18–20 tok/s** | ~118 tok/s | ~5 s | **Chosen.** Fastest, fits beside whisper and the server |
| Gemma 4 E2B `q8_0` (`Gemma4/gemma4-e2b-it-q8_0.gguf`) | 5,026 MB | not run | — | — | Kept on disk. CPU decode is memory-bandwidth-bound; Q8 moves ~1.7× the bytes of Q4 and would be proportionally slower for no audible quality gain in three-sentence answers |
| Gemma 3 4B `Q4_K_M` (`gemma3/`) | 2.4 GB | 14 tok/s (scene 38) | — | — | Control. Slower and not the requested family |
| Gemma 3 270m | 278 MB | — | — | — | Too weak to answer; useless as a prose draft |
| whisper.cpp `base.en` (`tools/whisper-cpp/models/ggml-base.en.bin`) | 141 MB | — | **~0.9 s for a 4 s clip** | <1 s | Chosen. Accurate on clear English at conversational distance |

Two findings from the smoke test that changed the implementation:

1. **Gemma 4 thinks by default.** The GGUF's Jinja template emits a thought channel, so a plain request returned an empty `content` and the whole 160-token budget in `reasoning_content`. The fix is `chat_template_kwargs: { enable_thinking: false }` plus `reasoning_format: "none"` on every request. Without it the room waits nine seconds and hears nothing.
2. **Gemma 4's chat template is not Gemma 3's.** Turns are `<|turn>role … <turn|>`, not `<start_of_turn>`. The service therefore never hand-rolls a prompt; it uses llama-server's `/v1/chat/completions` with `--jinja` so the GGUF's own template is applied.

## 3. Architecture as built

```mermaid
flowchart LR
    subgraph Browser
        BTN[Microphone button<br/>+ speaker name] --> CAP[getUserMedia → 16 kHz mono<br/>Float32 → 16-bit WAV]
        UI[Transcript · tokens as they stream]
        TTS[speechSynthesis<br/>sentence queue]
    end
    CAP -->|POST /api/room/transcribe<br/>loopback only| EP[Minimal API endpoint]
    EP --> WS[whisper-server.exe :8095<br/>base.en · CPU · 4 threads]
    WS -->|transcript| EP --> UI
    UI -->|Room.Ask speaker, text| SVC[RoomChatService singleton]
    subgraph Lane A — answering
        SVC --> LS[llama-server.exe :8094<br/>Gemma 4 E2B Q4 · CPU · 8 threads<br/>--jinja --cache-reuse 256 --spec-type ngram-mod]
        LS -->|SSE stream| SVC -->|Changed event| UI
        UI -->|completed sentences| TTS
    end
    subgraph Lane B — scribe, idle only
        SVC -->|after each answer| NOTES[Rewrite running notes<br/>from verbatim transcript]
        NOTES -->|markdown · verified: null| UI
    end
    BTN -.->|press cancels| NOTES
```

**Components**

| Piece | File | Role |
| --- | --- | --- |
| `RoomChatService` | `server/Services/RoomChatService.cs` | Launches and owns both host processes, streams answers, runs the scribe lane, holds the transcript. One `SemaphoreSlim` is the single model slot |
| Models | `server/Models/RoomChatModels.cs` | `RoomTurn` carries per-turn timings; `RoomNotes` carries version and status; `RoomState` is the render snapshot |
| Transcription endpoint | `server/Program.cs` → `POST /api/room/transcribe` | Accepts `audio/wav` (≤ 12 MB), forwards to whisper, returns `{ text, ms }`. Behind the existing loopback-only middleware |
| Browser capture and speech | `server/wwwroot/room.js` | `roomMic.start/stop/cancel` (ScriptProcessor → WAV, resamples if the browser refused 16 kHz); `roomSpeech.enqueue/stop` (a queue that does **not** cancel the sentence already playing) |
| Scene | `server/Components/Pages/Home.razor` case 39 (scene 40) | Setup table, microphone, typed fallback, transcript, latency table, notes, takeaways |
| Autopilot | `server/Services/AutopilotManifest.cs` steps `rm.01`–`rm.06` | Deterministic profile; `rm.02` is hard-blocked behind `Autopilot:AllowInProcessExecution` because it launches host processes |
| Shutdown | `Program.cs` `ApplicationStopping.Register(room.Stop)` | The two processes die with the server |

**The button is the design.** Pressing it starts capture and stamps the speaker's name on the turn; pressing again sends. There is no diarization — the presser *is* the label. Pressing also cancels any scribe job so the model slot is free the instant the transcript lands.

## 4. Where the latency went

| Lever | Implementation | Effect |
| --- | --- | --- |
| **Cap the answer** | System prompt: ≤ 3 sentences, no markdown, end with one question. The same two rules are appended to the current question, because a 2B model weights the last line most and was observed dropping the question-back when the rule lived only in the system prompt. `max_tokens 160` | At 20 tok/s the worst case is 8 s and the first sentence arrives in ~2 s |
| **Stable, cached prefix** | System prompt = instructions + grounding (the fixture README up to the scene table, plus `okf/index.md`). `cache_prompt: true`, server `--cache-reuse 256`. Only the last 10 completed turns are sent | The grounding (~1,700 tokens, ~14 s of prefill) is paid once at start-up; each question prefills only the new question and the previous answer |
| **Warm-up at start** | `StartAsync` issues a 4-token request after health so the grounding is in the KV cache before anyone speaks | First real question does not pay the 14 s |
| **Q4, not Q8** | `gemma-4-E2B-it-Q4_K_M.gguf` | ~20 tok/s vs an expected ~12 for Q8 |
| **Thinking off** | `enable_thinking: false`, `reasoning_format: none` | The budget is spent on words the room hears |
| **Stream and speak by sentence** | SSE deltas → `Changed` event → Blazor re-render coalesced to ~8/s; `LastSentenceEnd` finds `. ? !` followed by whitespace (≥ 20 chars in) and enqueues each sentence once. **Only the tab that asked speaks**: `speechSynthesis` is one queue per browser, so with two tabs open every tab speaking meant every sentence twice | The room hears sentence one while sentences two and three are still generating |
| **n-gram draft** | `--spec-type ngram-mod` | No second model, no memory; +12 % on prose in scene 38, more when the answer quotes the notes |
| **CPU, 8 threads** | `-t 8` (physical cores), `-ngl 0` | iGPU measured slower |
| **Transcribe fast** | whisper base.en, `-sns` (suppress non-speech), 0.4 s minimum clip | ~1 s per 10 s of speech |

### Latency budget vs. target

| Stage | Target | Measured on this laptop |
| --- | --- | --- |
| Button release → transcript | ≤ 1 s | 0.9–1.0 s for a 4 s question (whisper + endpoint) |
| First word on screen | ≤ 1.5 s | 323–342 ms when only the question is uncached; 1,293 ms when the previous answer must be prefilled too |
| First spoken sentence | ≤ 3 s | first-word time + ~20 tokens at ~18 tok/s ≈ +1 s |
| Whole three-sentence answer | ≤ 10 s | 44–68 tokens in 2.7–5.1 s |
| Decode speed | ≥ 15 tok/s | 17.1–20.3 tok/s |
| Prompt served from cache | ≥ 80 % | 98 % (1,741 of 1,764 tokens) |

The scene's latency table shows the last answer and the running median for each row, coloured against these targets. **A red row is the honest result on this hardware, not a defect.**

## 5. What is recorded, and what is trustworthy

| Artifact | Produced by | Trust |
| --- | --- | --- |
| **Transcript** — speaker, timestamp, text, STT ms, first-word ms, tokens, tok/s, cache hit | Deterministic (whisper output + stopwatches + llama.cpp counters) | The record |
| **Running notes** — Summary / Decisions / Open questions / Action items, then two lists computed by code from the transcript: the questions the team asked and the questions Gemma asked back | Gemma 4 writes the first four sections, rewritten from the whole verbatim transcript after every answer; the two question lists are extracted deterministically (a 2B model was observed mislabelling who asked what) | A draft. Rendered with OKF-style frontmatter `generated: model:gemma-4-e2b-it-Q4_K_M`, `verified: null`, `sources: [transcript]` |

The scribe lane runs only when nobody is asking: it takes the model slot with `Wait(0)` (never queues behind an answer) and its HTTP request is cancelled by the next press of the microphone. Rewriting from the full transcript each time means the notes cannot drift from what was said, at the cost of a longer prompt that is paid only in idle time.

State is held in memory in the singleton and discarded when the server stops, like every other scene. Writing the session folder (`transcript.md`, `notes.md`) to disk is the obvious next step and deliberately not done until the data-tier decision in §7 is made.

## 6. The topic and the grounding

The topic is **Open Knowledge Format (OKF)** because the platform already carries an authoritative, in-repo explanation written for exactly this audience: `training-fixture/README.md` (the consumer guide) and `okf/index.md`. Gemma is instructed to answer **only** from those notes, to say in one sentence when they do not cover something, and never to claim OKF trains a model. Good questions to try in the room:

- "What is OKF, in one breath?"
- "What does `stale_after` mean and what should a consumer do at that instant?"
- "Why is the API field name missing from the FUL-1842 bundle on purpose?"
- "Does reading a bundle train the model?"
- "What is the difference between `generated` and `verified`?"

Swapping the topic means swapping the grounding pack in `RoomChatService.BuildGrounding`. The pack needs an owner and a `stale_after`, like any other OKF artifact.

## 7. Governance

This is an agent under the programme's own definition: it makes model calls on the team's behalf and records what people say.

| Control | Decision |
| --- | --- |
| Registry (scene 22) | Blast radius sandbox · autonomy suggests · data tier Internal (meeting content) → **R1**. Pinned model: `gemma-4-E2B-it-Q4_K_M.gguf`; pinned STT: `ggml-base.en.bin`. Both hashes should be recorded before this leaves a laptop |
| Data boundary | Audio is posted to `127.0.0.1` only and discarded after transcription; the transcript stays in server memory. No outbound network from this scene |
| Data-tier gate (scene 27) | Must run over the transcript before any export to a share; not built because export is not built |
| Kill switch | **Stop the room** kills both processes; server shutdown kills them too |
| Autopilot | `rm.02` is hard-blocked unless `Autopilot:AllowInProcessExecution` is set, the same rule as mutation testing and the speculative-decoding run |

## 8. What was not done, and why

| Option | Reason |
| --- | --- |
| Speculative decoding with a draft model | Measured slower on prose on this laptop (scene 38) |
| Gemma 4 E2B Q8 | ~1.7× the bytes for CPU decode; speed matters more than the last percent of quality in a spoken answer. Kept on disk for a notes-lane experiment |
| Cloud models | Round-trip latency and confidential room content |
| The `.litertlm` Gemma 4 files in the repo | Intel LNL NPU build only; no AMD build. Unmeasured — a probe, not a plan |
| AMD Ryzen AI / Lemonade NPU | Gemma 4 support unverified |
| Speaker diarization | Too slow locally; the button replaces it |
| Browser Web Speech API for recognition | Edge and Chrome send audio to a vendor service; that breaks the "nothing leaves the machine" rule |
| Feeding the notes back into the answer prompt | Would change the cached prefix on every turn and cost the first-word time it was meant to save. The last 10 turns carry the conversation instead |
| Session folder on disk | Waits on the data-tier decision |

## 9. How to run it

1. Model weights and runtimes are gitignored. Required on disk:
   - `Gemma4/gemma-4-E2B-it-Q4_K_M.gguf` (from `cstr/gemma4-e2b-it-GGUF` on Hugging Face)
   - `tools/llama-cpp/cpu/llama-server.exe` (llama.cpp b10909 CPU build, or set `LLAMA_CPP_HOME`)
   - `tools/whisper-cpp/Release/whisper-server.exe` (whisper.cpp b5130 `whisper-bin-x64.zip`)
   - `tools/whisper-cpp/models/ggml-base.en.bin` (from `ggerganov/whisper.cpp` on Hugging Face)
2. Start the server as usual; the start-up log prints `LLM in the Room: …` with what is present or missing.
3. Open scene 40, press **Start the room** (≈ 20 s: two process launches plus the grounding pre-fill), then press the microphone, ask, and press again to send. Use Edge or Chrome; VS Code's embedded browser has no microphone or speech.
4. About 3.5 GB of RAM while running. **Stop the room** releases it.

## 10. Measurement protocol before quoting a number

1. Ask the same five questions in §6 three times each with the room warm.
2. Record first-word ms, total ms, tok/s and cache-hit % from the latency table; report the median and the worst.
3. Repeat with the Q8 model swapped in (`RoomChatService._model`) to confirm the Q4 choice with a number rather than an argument.
4. Only then quote a latency, with the hardware and the sample size attached.
