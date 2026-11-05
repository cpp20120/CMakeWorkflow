using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Campaigns;

public static class CampaignCommand
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Length < 2)
        {
            throw new ArgumentException(
                "Usage: CMakeWorkflow dagflow <mutation run|weak-memory run|fuzz seed|fuzz run|fuzz triage|fuzz merge> ...");
        }

        string[] allowed = (args[0], args[1]) switch
        {
            ("mutation", "run") =>
            [
                "--source-dir", "--suite", "--cxx", "--seed",
                "--cases", "--steps", "--timeout", "--report"
            ],
            ("weak-memory", "run") =>
            [
                "--source-dir", "--protocol", "--relacy",
                "--cxx", "--timeout", "--report", "--mutant"
            ],
            ("fuzz", "seed") => ["--source-dir", "--layer", "--output"],
            ("fuzz", "run") =>
            [
                "--build-dir", "--layer", "--backend", "--duration",
                "--timeout", "--corpus", "--results-dir", "--config", "--max-len", "--replay-only"
            ],
            ("fuzz", "triage") => ["--run-dir"],
            ("fuzz", "merge") => ["--run-dir", "--output", "--coverage"],
            _ => throw new ArgumentException($"Unknown DagFlow campaign: {args[0]} {args[1]}")
        };
        for (int index = 2; index < args.Length; index++)
        {
            string option = args[index];
            if (!allowed.Contains(option))
            {
                throw new ArgumentException($"Unknown option: {option}");
            }

            if (option is "--replay-only" or "--coverage")
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{option} requires a value.");
            }

            index++;
        }

        string source = Option(args, "--source-dir") ?? Directory.GetCurrentDirectory();
        source = Path.GetFullPath(source);
        return (args[0], args[1]) switch
        {
            ("mutation", "run") => await CampaignService.MutationAsync(new MutationOptions(source,
                Option(args, "--suite") ?? "all", Option(args, "--cxx") ?? "c++",
                Option(args, "--seed") ?? "496833221358",
                Integer(args, "--cases", Option(args, "--suite") == "queue" ? 32 : 48),
                Integer(args, "--steps", Option(args, "--suite") == "queue" ? 36 : 48),
                Integer(args, "--timeout", Option(args, "--suite") == "queue" ? 20 : 30),
                Option(args, "--report")), ct),
            ("weak-memory", "run") => await CampaignService.WeakMemoryAsync(new WeakMemoryOptions(source,
                Option(args, "--protocol") ?? "all",
                Option(args, "--relacy") ?? throw new ArgumentException("--relacy is required."),
                Option(args, "--cxx") ?? "c++", Integer(args, "--timeout", 60),
                Option(args, "--report"), Option(args, "--mutant")), ct),
            ("fuzz", "seed") => await CampaignService.FuzzSeedAsync(new FuzzSeedOptions(
                Option(args, "--layer") ?? throw new ArgumentException("--layer is required."),
                Option(args, "--output") ?? Path.Combine(source, "fuzz/corpus")), ct),
            ("fuzz", "run") => await CampaignService.FuzzRunAsync(new FuzzRunOptions(
                Option(args, "--build-dir") ?? throw new ArgumentException("--build-dir is required."),
                Option(args, "--layer") ?? throw new ArgumentException("--layer is required."),
                Option(args, "--backend"), Option(args, "--corpus"),
                Option(args, "--results-dir") ?? "out/fuzz-runs", Option(args, "--config"),
                Integer(args, "--duration", 60), Integer(args, "--timeout", 15),
                Integer(args, "--max-len", 4096), args.Contains("--replay-only")), ct),
            ("fuzz", "triage") => await CampaignService.FuzzTriageAsync(new FuzzTriageOptions(
                Option(args, "--run-dir") ?? throw new ArgumentException("--run-dir is required.")), ct),
            ("fuzz", "merge") => await CampaignService.FuzzMergeAsync(new FuzzMergeOptions(
                Option(args, "--run-dir") ?? throw new ArgumentException("--run-dir is required."),
                Option(args, "--output"), args.Contains("--coverage")), ct),
            _ => throw new ArgumentException($"Unknown DagFlow campaign: {args[0]} {args[1]}")
        };
    }

    internal static async Task<int> RunWithCompletionAsync(string reportPath, string campaign,
        Func<Task<int>> execute)
    {
        reportPath = Path.GetFullPath(reportPath);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string status = "failed";
        string marker = Path.Combine(Path.GetDirectoryName(reportPath)!, "campaign-completion.json");
        if (File.Exists(marker))
        {
            File.Delete(marker);
        }

        try
        {
            int code = await execute();
            status = code == 0 ? "succeeded" : "failed";
            return code;
        }
        catch (OperationCanceledException)
        {
            status = "canceled";
            throw;
        }
        finally
        {
            if (File.Exists(reportPath)
                && File.GetLastWriteTimeUtc(reportPath) >= started.UtcDateTime)
            {

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
                CampaignCompletion value = new(1, campaign, Path.GetFileName(reportPath),
                                    Convert.ToHexString(SHA256.HashData(
                                        await File.ReadAllBytesAsync(reportPath))).ToLowerInvariant(),
                                    started, DateTimeOffset.UtcNow, status);
#pragma warning restore CA1308

                string temporary = marker + ".tmp";
                await File.WriteAllTextAsync(temporary,
                    JsonSerializer.Serialize(value, CampaignCompletionJson.Default.CampaignCompletion) + "\n");
                File.Move(temporary, marker, overwrite: true);
            }
        }
    }

    private static int Integer(string[] args, string name, int fallback) =>
        Option(args, name) is { } text
            ? int.TryParse(text, out int value)
                ? value
                : throw new ArgumentException($"{name} must be an integer.")
            : fallback;

    private static string? Option(string[] args, string name)
    {
        string? result = null;
        for (int index = 2; index < args.Length; index++)
        {
            if (args[index] != name)
            {
                continue;
            }

            if (index + 1 >= args.Length || args[index + 1].StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"{name} requires a value.");
            }

            result = args[index + 1];
        }

        return result;
    }
}
