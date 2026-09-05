using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PocInteractiveTraining.Server.Models;

namespace PocInteractiveTraining.Server.Services;

// Compiles and runs a model's migrated code so correctness is decided by execution, not by a judge.
//
// This executes code an LLM wrote, so it is gated: a static allow-list rejects I/O, networking,
// process control, reflection and unsafe blocks before anything is compiled, and every call runs on a
// worker thread with a hard timeout. That is adequate for a loopback-only training tool. It is NOT a
// security boundary and must not be exposed to untrusted input or run on a shared host.
public sealed class MigrationSandbox
{
    private const int TimeoutMs = 5_000;

    private static readonly string[] BannedNamespaces =
    [
        "System.IO", "System.Net", "System.Diagnostics", "System.Reflection", "System.Threading",
        "System.Runtime.InteropServices", "System.Runtime.Loader", "Microsoft.Win32", "System.Security"
    ];

    private static readonly string[] BannedTokens =
    [
        "unsafe", "DllImport", "Process", "File.", "Directory.", "HttpClient", "Assembly.",
        "AppDomain", "Environment.Exit", "typeof(", "GetType()"
    ];

    public MigrationRunResult Run(string source, string entryType, string entryMethod, IReadOnlyList<string> inputs)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return new MigrationRunResult(false, "No code was produced.", [], []);
        }

        if (Screen(source) is { } refusal)
        {
            return new MigrationRunResult(false, refusal, [], []);
        }

        var tree = CSharpSyntaxTree.ParseText(source);
        var references = new[]
        {
            typeof(object).Assembly, typeof(decimal).Assembly, typeof(Enumerable).Assembly,
            typeof(System.Runtime.GCSettings).Assembly
        }
        .Select(assembly => MetadataReference.CreateFromFile(assembly.Location))
        .Concat([MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll"))])
        .Distinct()
        .ToList();

        var compilation = CSharpCompilation.Create(
            "MigrationArm" + Guid.NewGuid().ToString("N")[..8],
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, optimizationLevel: OptimizationLevel.Release));

        using var stream = new MemoryStream();
        var emit = compilation.Emit(stream);
        if (!emit.Success)
        {
            var errors = emit.Diagnostics
                .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                .Select(diagnostic => $"{diagnostic.Id} line {diagnostic.Location.GetLineSpan().StartLinePosition.Line + 1}: {diagnostic.GetMessage()}")
                .Take(8)
                .ToList();
            return new MigrationRunResult(false, "The migrated code did not compile.", errors, []);
        }

        stream.Position = 0;
        var context = new AssemblyLoadContext("migration-arm", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(stream);
            var method = ResolveEntryPoint(assembly, entryType, entryMethod);
            if (method is null)
            {
                return new MigrationRunResult(false,
                    $"No entry point found. Expected a public static method '{entryMethod}' taking one string.", [], []);
            }

            var outputs = new List<MigrationOutput>(inputs.Count);
            foreach (var input in inputs)
            {
                outputs.Add(Invoke(method, input));
            }

            return new MigrationRunResult(true, "", [], outputs);
        }
        catch (Exception ex)
        {
            return new MigrationRunResult(false, $"The migrated code could not be loaded: {ex.Message}", [], []);
        }
        finally
        {
            context.Unload();
        }
    }

    private static MethodInfo? ResolveEntryPoint(Assembly assembly, string entryType, string entryMethod)
    {
        bool Matches(MethodInfo candidate) =>
            candidate.IsStatic && candidate.IsPublic &&
            candidate.GetParameters().Length == 1 &&
            candidate.GetParameters()[0].ParameterType == typeof(string);

        var preferred = assembly.GetTypes()
            .FirstOrDefault(type => string.Equals(type.Name, entryType, StringComparison.OrdinalIgnoreCase))
            ?.GetMethods()
            .FirstOrDefault(candidate => Matches(candidate) && string.Equals(candidate.Name, entryMethod, StringComparison.OrdinalIgnoreCase));

        return preferred ?? assembly.GetTypes()
            .SelectMany(type => type.GetMethods())
            .FirstOrDefault(candidate => Matches(candidate) && string.Equals(candidate.Name, entryMethod, StringComparison.OrdinalIgnoreCase));
    }

    private static MigrationOutput Invoke(MethodInfo method, string input)
    {
        object? value = null;
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                value = method.Invoke(null, [input]);
            }
            catch (TargetInvocationException ex)
            {
                failure = ex.InnerException ?? ex;
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        { IsBackground = true };

        worker.Start();
        if (!worker.Join(TimeoutMs))
        {
            return new MigrationOutput(input, "", $"timed out after {TimeoutMs / 1000}s");
        }

        return failure is not null
            ? new MigrationOutput(input, "", failure.GetType().Name + ": " + failure.Message)
            : new MigrationOutput(input, Format(value), null);
    }

    private static string Format(object? value) => value switch
    {
        null => "null",
        decimal d => d.ToString("0.00"),
        double d => d.ToString("0.00"),
        float f => f.ToString("0.00"),
        _ => value.ToString() ?? ""
    };

    // Rejects anything that reaches outside pure computation. Returns null when the code is acceptable.
    private static string? Screen(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var root = tree.GetRoot();

        foreach (var directive in root.DescendantNodes().OfType<UsingDirectiveSyntax>())
        {
            var name = directive.Name?.ToString() ?? "";
            var banned = BannedNamespaces.FirstOrDefault(ns => name.StartsWith(ns, StringComparison.Ordinal));
            if (banned is not null)
            {
                return $"Refused to run: the code imports {name}. Migrated logic must be pure computation.";
            }
        }

        foreach (var token in BannedTokens)
        {
            if (source.Contains(token, StringComparison.Ordinal))
            {
                return $"Refused to run: the code references '{token}'. Migrated logic must be pure computation.";
            }
        }

        return null;
    }
}
