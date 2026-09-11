using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Drives a local llama.cpp server to measure speculative decoding on this machine. Each arm is a fresh
// process so nothing carries over; the numbers are llama.cpp's own timings, not ours.
public sealed class LocalInferenceService
{
    private const int Port = 8093;
    private static readonly SemaphoreSlim OneRunAtATime = new(1, 1);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    private readonly string _server;
    private readonly string _target;
    private readonly string _draft;

    public LocalInferenceService(string root)
    {
        var home = System.Environment.GetEnvironmentVariable("LLAMA_CPP_HOME");
        _server = home is not null && File.Exists(Path.Combine(home, "llama-server.exe"))
            ? Path.Combine(home, "llama-server.exe")
            : Path.GetFullPath(Path.Combine(root, "tools", "llama-cpp", "cpu", "llama-server.exe"));
        _target = Path.GetFullPath(Path.Combine(root, "gemma3", "gemma-3-4b-it-Q4_K_M.gguf"));
        _draft = Path.GetFullPath(Path.Combine(root, "gemma3", "gemma-3-270m-it-Q8_0.gguf"));
    }

    public static readonly (string Kind, string Title, string Prompt, string Why, int MaxTokens)[] Prompts =
    [
        ("code", "Java from a COBOL MOVE chain",
            "Write a Java method `public static String formatPrice(int price)` that reproduces this COBOL: MOVE PRICE TO CONVERT-PRICE where CONVERT-PRICE is PIC 9(5), then MOVE CONVERT-PRICE TO PRICEO where PRICEO is PIC X(10). Include a JUnit test with cases 23499, 123456, 5, -1500 and 0. Code only.",
            "Fresh code generation. Imports, braces and assertion boilerplate are predictable, so the small draft should do well; the n-gram draft has nothing to copy from yet.", 192),
        ("edit", "Rename a field in an existing Java class",
            "Rename the field `dealerId` to `dealerCode` everywhere in this Java class and return the complete class, unchanged otherwise. Code only.\n\npublic final class InventoryRow {\n    private final String vin;\n    private final int autoYear;\n    private final String model;\n    private final long price;\n    private final int dealerId;\n    private final boolean newAuto;\n\n    public InventoryRow(String vin, int autoYear, String model, long price, int dealerId, boolean newAuto) {\n        this.vin = vin;\n        this.autoYear = autoYear;\n        this.model = model;\n        this.price = price;\n        this.dealerId = dealerId;\n        this.newAuto = newAuto;\n    }\n\n    public String getVin() { return vin; }\n    public int getAutoYear() { return autoYear; }\n    public String getModel() { return model; }\n    public long getPrice() { return price; }\n    public int getDealerId() { return dealerId; }\n    public boolean isNewAuto() { return newAuto; }\n\n    public String formatPrice() {\n        long truncated = Math.abs(price) % 100000L;\n        return String.format(\"%-10s\", String.format(\"%05d\", truncated));\n    }\n}",
            "Most of the answer is the input copied back. This is the task speculative decoding was made for, and the one an IDE assistant does all day.", 320),
        ("prose", "Executive explanation",
            "In plain English for a non-technical executive, explain in about 150 words why a company should measure AI cost per successful outcome rather than cost per token.",
            "Free prose has many equally good next words. The draft guesses wrong more often, so acceptance should fall and the speed-up with it.", 192)
    ];

    public static readonly (string Arm, string Label, string How)[] Arms =
    [
        ("baseline", "Gemma 3 4B alone", "One token per forward pass. The reference for speed and for text."),
        ("draft-270m", "Gemma 3 270m drafts, 4B verifies", "The 270m model proposes up to 8 tokens; the 4B model checks them all in one pass and keeps the prefix it agrees with."),
        ("ngram-mod", "N-gram lookup drafts, 4B verifies", "No second model: tokens already generated are used to guess what comes next. Costs no extra memory.")
    ];

    public SpecEnvironment Environment()
    {
        var (total, free) = Memory();
        var available = File.Exists(_server) && File.Exists(_target) && File.Exists(_draft);
        var status = available
            ? "llama.cpp CPU build and both GGUF files present."
            : string.Join(" ", new[]
            {
                File.Exists(_server) ? null : $"llama-server.exe not found at {_server}.",
                File.Exists(_target) ? null : $"Target model missing: {_target}.",
                File.Exists(_draft) ? null : $"Draft model missing: {_draft}."
            }.Where(part => part is not null));

        return new SpecEnvironment(
            available,
            status,
            _server,
            "CPU (llama.cpp b10909, AVX-512)",
            Path.GetFileName(_target),
            SizeMb(_target),
            Path.GetFileName(_draft),
            SizeMb(_draft),
            System.Environment.ProcessorCount,
            total,
            free);
    }

    // Runs the three arms sequentially and reports each as it lands. Greedy sampling so text differences
    // are attributable to the decoding path, not to randomness.
    public async Task RunAsync(string promptKind, Action<SpecArmResult> onArm, CancellationToken cancellation)
    {
        var prompt = Prompts.First(p => p.Kind == promptKind);
        if (!await OneRunAtATime.WaitAsync(0, cancellation))
        {
            throw new InvalidOperationException("Another local run is in progress; the laptop can hold one 4B model at a time.");
        }

        try
        {
            SpecArmResult? baseline = null;
            foreach (var (arm, label, _) in Arms)
            {
                var result = await RunArmAsync(arm, label, prompt.Prompt, prompt.MaxTokens, cancellation);
                if (arm == "baseline")
                {
                    baseline = result;
                }
                else if (baseline is { Text: not null } && result.Text is not null)
                {
                    var identical = string.Equals(baseline.Text, result.Text, StringComparison.Ordinal);
                    result = result with { IdenticalToBaseline = identical, DivergesAtChar = identical ? null : FirstDifference(baseline.Text, result.Text) };
                }

                onArm(result);
            }
        }
        finally
        {
            OneRunAtATime.Release();
        }
    }

    private async Task<SpecArmResult> RunArmAsync(string arm, string label, string prompt, int maxTokens, CancellationToken cancellation)
    {
        var args = new List<string> { "-m", _target, "--port", Port.ToString(), "-c", "4096", "-ngl", "0", "--no-warmup", "--log-disable" };
        switch (arm)
        {
            case "draft-270m":
                args.AddRange(["-md", _draft, "-ngld", "0", "--spec-type", "draft-simple", "--spec-draft-n-max", "8", "--spec-draft-p-min", "0.75"]);
                break;
            case "ngram-mod":
                args.AddRange(["--spec-type", "ngram-mod"]);
                break;
        }

        var start = new ProcessStartInfo(_server)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(_server)
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null && stderr.Length < 8000) stderr.AppendLine(e.Data); };
        process.OutputDataReceived += (_, _) => { };
        try
        {
            process.Start();
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            var healthy = await WaitHealthyAsync(process, cancellation);
            if (!healthy)
            {
                return Failed(arm, label, "llama-server did not become healthy within 120 s. " + Tail(stderr));
            }

            var body = new
            {
                prompt = $"<start_of_turn>user\n{prompt}<end_of_turn>\n<start_of_turn>model\n",
                n_predict = maxTokens,
                temperature = 0,
                seed = 1,
                cache_prompt = false
            };

            // The warm-up settles page faults on the freshly mapped weights. It must NOT be the measured prompt:
            // the n-gram draft caches every token the server has produced, so a same-prompt warm-up would hand
            // it the whole answer and report a perfect acceptance rate that is a leak, not a result.
            var warmup = new { prompt = "<start_of_turn>user\nName three primary colours.<end_of_turn>\n<start_of_turn>model\n", n_predict = 24, temperature = 0, seed = 1, cache_prompt = false };
            using (await Http.PostAsJsonAsync($"http://127.0.0.1:{Port}/completion", warmup, cancellation)) { }
            using var response = await Http.PostAsJsonAsync($"http://127.0.0.1:{Port}/completion", body, cancellation);
            var json = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: cancellation);
            var timings = json.GetProperty("timings");
            var text = json.GetProperty("content").GetString() ?? "";
            var drafted = Int(timings, "draft_n");
            var accepted = Int(timings, "draft_n_accepted");
            process.Refresh();
            var workingSet = (int)(process.WorkingSet64 / (1024 * 1024));

            return new SpecArmResult(
                arm,
                label,
                "completed",
                Int(timings, "predicted_n"),
                Math.Round(Dbl(timings, "predicted_per_second"), 1),
                (int)Dbl(timings, "predicted_ms"),
                Math.Round(Dbl(timings, "prompt_per_second"), 0),
                drafted,
                accepted,
                drafted > 0 ? (int)Math.Round(100.0 * accepted / drafted) : null,
                workingSet,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12],
                text,
                arm == "baseline" ? true : null,
                null,
                null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Failed(arm, label, ex.Message + " " + Tail(stderr));
        }
        finally
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            await Task.Delay(800, CancellationToken.None);
        }
    }

    private static async Task<bool> WaitHealthyAsync(Process process, CancellationToken cancellation)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline && !process.HasExited)
        {
            await Task.Delay(500, cancellation);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                cts.CancelAfter(2000);
                using var response = await Http.GetAsync($"http://127.0.0.1:{Port}/health", cts.Token);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
            }
        }

        return false;
    }

    private static SpecArmResult Failed(string arm, string label, string error) =>
        new(arm, label, "failed", 0, 0, 0, 0, 0, 0, null, 0, null, null, null, null, error.Trim());

    private static int FirstDifference(string a, string b)
    {
        var i = 0;
        var limit = Math.Min(a.Length, b.Length);
        while (i < limit && a[i] == b[i])
        {
            i++;
        }

        return i;
    }

    private static string Tail(StringBuilder buffer)
    {
        var text = buffer.ToString().Trim();
        return text.Length <= 400 ? text : text[^400..];
    }

    private static int Int(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? (int)value.GetDouble() : 0;

    private static double Dbl(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;

    private static long SizeMb(string path) => File.Exists(path) ? new FileInfo(path).Length / (1024 * 1024) : 0;

    public static (long TotalMb, long FreeMb) Memory()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var info = GC.GetGCMemoryInfo();
            return (info.TotalAvailableMemoryBytes / (1024 * 1024), 0);
        }

        var status = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        return GlobalMemoryStatusEx(ref status)
            ? ((long)(status.ullTotalPhys / (1024 * 1024)), (long)(status.ullAvailPhys / (1024 * 1024)))
            : (0, 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
