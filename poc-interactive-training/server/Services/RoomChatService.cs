using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Runs Gemma 4 E2B and whisper.cpp on this laptop so a team can talk to a model in the room with no
// cloud and no bridge. Two lanes share one model slot: answering (foreground, streamed) and note-taking
// (background, cancelled the moment someone presses the microphone). Latency is measured, not claimed.
public sealed class RoomChatService : IDisposable
{
    private const int LlamaPort = 8094;
    private const int WhisperPort = 8095;
    private const int MaxTurnsInPrompt = 10;
    private const int MaxAnswerTokens = 160;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _llamaExe;
    private readonly string _model;
    private readonly string _whisperExe;
    private readonly string _whisperModel;
    private readonly string _grounding;
    private readonly int _threads;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _modelLane = new(1, 1);
    private readonly List<RoomTurn> _turns = [];

    private Process? _llama;
    private Process? _whisper;
    private string _status = "offline";
    private string? _error;
    private int _nextId;
    private int _groundingTokens;
    private RoomNotes _notes = new("", 0, null, "idle", null);
    private CancellationTokenSource? _notesCts;

    public const string Topic = "Open Knowledge Format (OKF)";

    public event Action? Changed;

    public RoomChatService(string root, TrainingFixtureProvider fixture)
    {
        var home = System.Environment.GetEnvironmentVariable("LLAMA_CPP_HOME");
        _llamaExe = home is not null && File.Exists(Path.Combine(home, "llama-server.exe"))
            ? Path.Combine(home, "llama-server.exe")
            : Path.GetFullPath(Path.Combine(root, "tools", "llama-cpp", "cpu", "llama-server.exe"));
        _model = Path.GetFullPath(Path.Combine(root, "Gemma4", "gemma-4-E2B-it-Q4_K_M.gguf"));
        _whisperExe = Path.GetFullPath(Path.Combine(root, "tools", "whisper-cpp", "Release", "whisper-server.exe"));
        _whisperModel = Path.GetFullPath(Path.Combine(root, "tools", "whisper-cpp", "models", "ggml-base.en.bin"));
        _threads = Math.Clamp(System.Environment.ProcessorCount / 2, 4, 8);
        _grounding = BuildGrounding(fixture);
    }

    // The tutor is grounded on the fixture's own OKF explanation, not on what the model remembers. The
    // table describing other scenes is cut so the prefix stays small and every fact is about the topic.
    private static string BuildGrounding(TrainingFixtureProvider fixture)
    {
        var readme = fixture.Read("README.md");
        var cut = readme.IndexOf("## Where other scenes get their context", StringComparison.Ordinal);
        if (cut > 0)
        {
            readme = readme[..cut];
        }

        return readme.Trim() + "\n\n---\n\n# okf/index.md\n\n" + fixture.Read("okf/index.md").Trim();
    }

    private string SystemPrompt => $$"""
        You are Gemma, a tutor sitting in the room with a software team. Your one topic is {{Topic}}.
        Everyone hears your answer read aloud, so:
        - Answer in at most three short, plain-English sentences. No headings, no bullet lists, no markdown.
        - Use only the reference notes below. If the notes do not cover something, say so in one sentence.
        - End every answer with exactly one short question back to the team that checks understanding or moves the topic forward.
        - Never claim OKF trains or fine-tunes a model; it supplies inference-time context.

        Reference notes:
        {{_grounding}}
        """;

    public RoomEnvironment Environment()
    {
        var (total, free) = LocalInferenceService.Memory();
        var missing = new[]
        {
            File.Exists(_llamaExe) ? null : $"llama-server.exe not found at {_llamaExe}.",
            File.Exists(_model) ? null : $"Gemma 4 model missing: {_model}.",
            File.Exists(_whisperExe) ? null : $"whisper-server.exe not found at {_whisperExe}.",
            File.Exists(_whisperModel) ? null : $"Whisper model missing: {_whisperModel}."
        }.Where(part => part is not null).ToList();

        return new RoomEnvironment(
            missing.Count == 0,
            missing.Count == 0 ? "Gemma 4 E2B Q4, llama.cpp CPU build and whisper.cpp base.en present." : string.Join(" ", missing),
            Path.GetFileName(_model),
            SizeMb(_model),
            Path.GetFileName(_whisperModel),
            SizeMb(_whisperModel),
            _threads,
            total,
            free);
    }

    public RoomState Snapshot()
    {
        lock (_gate)
        {
            return new RoomState(_status, Environment(), _turns.ToList(), _notes, Topic, _groundingTokens, _error);
        }
    }

    public bool IsReady => _status is "ready" or "answering" or "noting";

    // Starts both local servers and pre-fills the grounding so the first question does not pay for it.
    public async Task StartAsync(CancellationToken cancellation)
    {
        if (!Environment().Available)
        {
            Set("error", Environment().Status);
            return;
        }

        lock (_gate)
        {
            if (_status is not ("offline" or "error"))
            {
                return;
            }

            _status = "starting";
            _error = null;
        }

        Raise();
        try
        {
            _llama ??= await LaunchAsync(_llamaExe,
                ["-m", _model, "--port", LlamaPort.ToString(), "-c", "6144", "-ngl", "0", "-t", _threads.ToString(),
                 "--jinja", "--cache-reuse", "256", "--spec-type", "ngram-mod", "--no-warmup", "--log-disable"],
                $"http://127.0.0.1:{LlamaPort}/health", cancellation);
            _whisper ??= await LaunchAsync(_whisperExe,
                ["-m", _whisperModel, "--port", WhisperPort.ToString(), "-t", "4", "-nt", "-sns"],
                $"http://127.0.0.1:{WhisperPort}/", cancellation);

            Set("warming", null);
            Raise();
            var warm = await CompleteAsync([new { role = "user", content = "Say the word ready." }], 4, null, cancellation);
            lock (_gate)
            {
                _groundingTokens = warm.PromptTokens;
                _status = "ready";
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Stop();
            Set("error", ex.Message);
        }

        Raise();
    }

    public void Stop()
    {
        _notesCts?.Cancel();
        Kill(ref _llama);
        Kill(ref _whisper);
        lock (_gate)
        {
            _status = "offline";
        }

        Raise();
    }

    public void ClearConversation()
    {
        _notesCts?.Cancel();
        lock (_gate)
        {
            _turns.Clear();
            _notes = new RoomNotes("", 0, null, "idle", null);
            _error = null;
            if (_status is "answering" or "noting")
            {
                _status = "ready";
            }
        }

        Raise();
    }

    public async Task<(string Text, int Milliseconds)> TranscribeAsync(byte[] wav, CancellationToken cancellation)
    {
        if (_whisper is null)
        {
            throw new InvalidOperationException("whisper-server is not running. Start the room first.");
        }

        var watch = Stopwatch.StartNew();
        using var form = new MultipartFormDataContent();
        var audio = new ByteArrayContent(wav);
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audio, "file", "speech.wav");
        form.Add(new StringContent("json"), "response_format");
        form.Add(new StringContent("0.0"), "temperature");
        using var response = await Http.PostAsync($"http://127.0.0.1:{WhisperPort}/inference", form, cancellation);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellation);
        var text = JsonDocument.Parse(json).RootElement.TryGetProperty("text", out var value) ? value.GetString() ?? "" : "";
        return (CleanTranscript(text), (int)watch.ElapsedMilliseconds);
    }

    // Records the question immediately and streams the answer; the caller sees tokens as they land.
    public void Ask(string speaker, string text, int sttMs)
    {
        text = text.Trim();
        if (text.Length == 0 || !IsReady)
        {
            return;
        }

        _notesCts?.Cancel();
        speaker = string.IsNullOrWhiteSpace(speaker) ? "Team" : speaker.Trim();
        int answerId;
        lock (_gate)
        {
            _turns.Add(new RoomTurn(++_nextId, "user", speaker, text, "completed", DateTimeOffset.UtcNow, sttMs, 0, 0, 0, 0, 0, 0, null));
            answerId = ++_nextId;
            _turns.Add(new RoomTurn(answerId, "assistant", "Gemma 4", "", "streaming", DateTimeOffset.UtcNow, 0, 0, 0, 0, 0, 0, 0, null));
            _status = "answering";
            _error = null;
        }

        Raise();
        _ = Task.Run(() => AnswerAsync(answerId));
    }

    private async Task AnswerAsync(int answerId)
    {
        await _modelLane.WaitAsync();
        try
        {
            var messages = new List<object>();
            lock (_gate)
            {
                var history = _turns.Where(t => t.Id < answerId && t.Status == "completed").TakeLast(MaxTurnsInPrompt).ToList();
                for (var i = 0; i < history.Count; i++)
                {
                    var turn = history[i];
                    var content = turn.Role == "user" ? $"{turn.Speaker}: {turn.Text}" : turn.Text;
                    // A 2B model weights the last line most, so the two rules that matter travel with the question.
                    if (i == history.Count - 1 && turn.Role == "user")
                    {
                        content += "\n\n(Reply in at most three short sentences from the reference notes, then finish with one short question for the team.)";
                    }

                    messages.Add(new { role = turn.Role, content });
                }
            }

            var result = await CompleteAsync(messages, MaxAnswerTokens, partial => Update(answerId, t => t with { Text = partial }), CancellationToken.None);
            Update(answerId, t => t with
            {
                Text = result.Text.Trim(),
                Status = "completed",
                FirstTokenMs = result.FirstTokenMs,
                TotalMs = result.TotalMs,
                PromptTokens = result.PromptTokens,
                CachedTokens = result.CachedTokens,
                OutputTokens = result.OutputTokens,
                TokPerSec = result.TokPerSec
            });
        }
        catch (Exception ex)
        {
            Update(answerId, t => t with { Status = "failed", Error = ex.Message });
            Set(null, ex.Message);
        }
        finally
        {
            _modelLane.Release();
            lock (_gate)
            {
                if (_status == "answering")
                {
                    _status = "ready";
                }
            }

            Raise();
        }

        _ = Task.Run(UpdateNotesAsync);
    }

    // The scribe lane: rewrites the running notes from the verbatim transcript. It is cancelled by the
    // next question so it never delays an answer, and its output is labelled generated, not verified.
    private async Task UpdateNotesAsync()
    {
        var cts = new CancellationTokenSource();
        _notesCts = cts;
        string transcript;
        string askedByGemma;
        string askedByTeam;
        int version;
        lock (_gate)
        {
            if (_turns.Count(t => t.Status == "completed") < 2)
            {
                return;
            }

            var done = _turns.Where(t => t.Status == "completed").ToList();
            transcript = string.Join("\n", done.Select(t =>
                t.Role == "user" ? $"[{t.Speaker}, a person in the room, asked] {t.Text}" : $"[Gemma, the model, answered] {t.Text}"));
            // Who asked what is a fact of the transcript, so it is listed by code rather than recalled by the model.
            askedByGemma = Bullets(done.Where(t => t.Role == "assistant").SelectMany(t => Questions(t.Text)));
            askedByTeam = Bullets(done.Where(t => t.Role == "user").Select(t => $"{t.Speaker}: {t.Text}"));
            version = _notes.Version + 1;
            _notes = _notes with { Status = "updating" };
            if (_status == "ready")
            {
                _status = "noting";
            }
        }

        Raise();
        // Never queue behind an answer: if the lane is busy the answer's own completion will re-run the scribe.
        if (!_modelLane.Wait(0))
        {
            lock (_gate) { _notes = _notes with { Status = "idle" }; if (_status == "noting") _status = "ready"; }
            Raise();
            return;
        }

        try
        {
            var prompt = $$"""
                You are the scribe for a team session about {{Topic}}. From the transcript below, write the running notes as markdown with exactly these headings and nothing else:
                ## Summary
                (two sentences)
                ## Decisions
                ## Open questions
                ## Action items
                Use short bullets. Only record what the transcript supports; write "none yet" under a heading with nothing to record.

                Transcript:
                {{transcript}}
                """;
            var result = await CompleteAsync([new { role = "user", content = prompt }], 400, null, cts.Token, systemOverride: "You write terse, accurate meeting notes.");
            var markdown = result.Text.Trim()
                + "\n\n## Questions the team asked (from the transcript)\n" + askedByTeam
                + "\n\n## Questions Gemma asked the team (from the transcript)\n" + askedByGemma;
            lock (_gate)
            {
                _notes = new RoomNotes(markdown, version, DateTimeOffset.UtcNow, "idle", null);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _notes = _notes with { Status = "idle" }; }
        }
        catch (Exception ex)
        {
            lock (_gate) { _notes = _notes with { Status = "idle", Error = ex.Message }; }
        }
        finally
        {
            _modelLane.Release();
            lock (_gate)
            {
                if (_status == "noting")
                {
                    _status = "ready";
                }
            }

            Raise();
        }
    }

    private sealed record Completion(string Text, int FirstTokenMs, int TotalMs, int PromptTokens, int CachedTokens, int OutputTokens, double TokPerSec);

    // OpenAI-compatible streaming against llama-server with the GGUF's own Jinja template. Gemma 4 thinks
    // by default and puts the answer in reasoning_content; thinking is switched off so the text arrives
    // as content and the latency is spent on words the room can hear.
    private async Task<Completion> CompleteAsync(IEnumerable<object> turns, int maxTokens, Action<string>? onPartial, CancellationToken cancellation, string? systemOverride = null)
    {
        var messages = new List<object> { new { role = "system", content = systemOverride ?? SystemPrompt } };
        messages.AddRange(turns);
        var body = new
        {
            messages,
            max_tokens = maxTokens,
            temperature = 0.3,
            top_p = 0.95,
            stream = true,
            cache_prompt = true,
            timings_per_token = true,
            reasoning_format = "none",
            chat_template_kwargs = new { enable_thinking = false }
        };

        var watch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{LlamaPort}/v1/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
        response.EnsureSuccessStatusCode();

        var text = new StringBuilder();
        var firstTokenMs = 0;
        int promptTokens = 0, cachedTokens = 0, outputTokens = 0;
        double tokPerSec = 0;
        using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var reader = new StreamReader(stream);
        while (await reader.ReadLineAsync(cancellation) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal) || line == "data: [DONE]")
            {
                continue;
            }

            using var chunk = JsonDocument.Parse(line[6..]);
            var root = chunk.RootElement;
            if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String && content.GetString() is { Length: > 0 } piece)
            {
                if (text.Length == 0)
                {
                    firstTokenMs = (int)watch.ElapsedMilliseconds;
                }

                text.Append(piece);
                onPartial?.Invoke(text.ToString());
            }

            if (root.TryGetProperty("timings", out var timings))
            {
                // llama.cpp reports prompt_n as tokens actually processed; the cached prefix is separate.
                cachedTokens = Int(timings, "cache_n");
                promptTokens = Int(timings, "prompt_n") + cachedTokens;
                outputTokens = Int(timings, "predicted_n");
                tokPerSec = Dbl(timings, "predicted_per_second");
            }
        }

        return new Completion(text.ToString(), firstTokenMs, (int)watch.ElapsedMilliseconds, promptTokens, cachedTokens, outputTokens, Math.Round(tokPerSec, 1));
    }

    private static async Task<Process> LaunchAsync(string exe, string[] args, string probeUrl, CancellationToken cancellation)
    {
        var start = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(exe)
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < 8000) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
        {
            await Task.Delay(400, cancellation);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(2000);
                // whisper-server has no health route; any HTTP answer means it is listening.
                using var response = await Http.GetAsync(probeUrl, cts.Token);
                if (probeUrl.EndsWith("/health", StringComparison.Ordinal) ? response.IsSuccessStatusCode : true)
                {
                    return process;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        var tail = stderr.ToString().Trim();
        throw new InvalidOperationException($"{Path.GetFileName(exe)} did not start listening within 120 s. {(tail.Length > 400 ? tail[^400..] : tail)}");
    }

    private static void Kill(ref Process? process)
    {
        if (process is null)
        {
            return;
        }

        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
        process.Dispose();
        process = null;
    }

    private void Update(int id, Func<RoomTurn, RoomTurn> change)
    {
        lock (_gate)
        {
            var index = _turns.FindIndex(t => t.Id == id);
            if (index >= 0)
            {
                _turns[index] = change(_turns[index]);
            }
        }

        Raise();
    }

    private void Set(string? status, string? error)
    {
        lock (_gate)
        {
            if (status is not null)
            {
                _status = status;
            }

            _error = error;
        }
    }

    private void Raise() => Changed?.Invoke();

    private static string CleanTranscript(string text)
    {
        var cleaned = text.Replace("\n", " ").Trim();
        // whisper labels silence and noise in brackets; those are not questions.
        return cleaned.StartsWith('[') && cleaned.EndsWith(']') ? "" : cleaned;
    }

    private static IEnumerable<string> Questions(string text) =>
        text.Split(['.', '?', '!'], StringSplitOptions.RemoveEmptyEntries)
            .Zip(text.Where(c => c is '.' or '?' or '!'), (sentence, mark) => (sentence: sentence.Trim(), mark))
            .Where(pair => pair.mark == '?' && pair.sentence.Length > 0)
            .Select(pair => pair.sentence + "?");

    private static string Bullets(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count == 0 ? "- none yet" : string.Join("\n", list.Select(item => "- " + item));
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : 0;

    private static double Dbl(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static long SizeMb(string path) => File.Exists(path) ? new FileInfo(path).Length / (1024 * 1024) : 0;

    public void Dispose()
    {
        Stop();
        _modelLane.Dispose();
    }
}
