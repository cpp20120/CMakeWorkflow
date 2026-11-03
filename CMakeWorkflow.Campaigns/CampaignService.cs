namespace CMakeWorkflow.Campaigns;

// Typed domain entry points; no argv parsing, subprocess CLI or transport dependency.
public sealed record MutationOptions(
    string SourceDirectory,
    string Suite = "all",
    string Compiler = "c++",
    string Seed = "496833221358",
    int Cases = 48,
    int Steps = 48,
    int TimeoutSeconds = 30,
    string? ReportPath = null);

public sealed record WeakMemoryOptions(
    string SourceDirectory,
    string Protocol,
    string RelacyDirectory,
    string Compiler = "c++",
    int TimeoutSeconds = 60,
    string? ReportPath = null,
    string? Mutant = null);

public sealed record FuzzSeedOptions(string Layer, string OutputDirectory);

public sealed record FuzzTriageOptions(string RunDirectory);

public sealed record FuzzMergeOptions(string RunDirectory, string? OutputDirectory = null, bool Coverage = false);

public static class CampaignService
{
    public static Task<int> MutationAsync(MutationOptions request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Suite) || string.IsNullOrWhiteSpace(request.Compiler)
                                                     || request.Cases is < 1 or > 100000 ||
                                                     request.Steps is < 1 or > 100000
                                                     || request.TimeoutSeconds is < 1 or > 86400)
            throw new ArgumentException("Invalid mutation campaign options.");
        var source = Path.GetFullPath(request.SourceDirectory);
        var report = Path.GetFullPath(request.ReportPath ?? Path.Combine(source,
            request.Suite == "queue" ? "out/queue-mutations/report.json" : "out/protocol-mutations/report.json"));
        return CampaignCommand.RunWithCompletionAsync(report, "mutation", () =>
            request.Suite == "queue"
                ? QueueMutationCampaign.RunAsync(source, request.Compiler, request.Seed,
                    request.Cases, request.Steps, request.TimeoutSeconds, report, ct)
                : MutationCampaign.RunAsync(source, request.Suite, request.Compiler,
                    request.Seed, request.Cases, request.Steps, request.TimeoutSeconds, report, ct));
    }

    public static Task<int> WeakMemoryAsync(WeakMemoryOptions request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Protocol) || string.IsNullOrWhiteSpace(request.RelacyDirectory)
                                                        || string.IsNullOrWhiteSpace(request.Compiler) ||
                                                        request.TimeoutSeconds is < 1 or > 86400
                                                        || request.Mutant is { Length: 0 })
            throw new ArgumentException("Invalid weak-memory campaign options.");
        var source = Path.GetFullPath(request.SourceDirectory);
        var report = Path.GetFullPath(request.ReportPath ?? Path.Combine(source, "out/weak-memory/report.json"));
        return CampaignCommand.RunWithCompletionAsync(report, "weak-memory", () =>
            WeakMemoryCampaign.RunAsync(source, request.Protocol, request.RelacyDirectory,
                request.Compiler, request.TimeoutSeconds, report, request.Mutant, ct));
    }

    public static Task<int> FuzzSeedAsync(FuzzSeedOptions request, CancellationToken ct) =>
        FuzzSeedCampaign.RunAsync(request.Layer, request.OutputDirectory, ct);

    public static Task<int> FuzzRunAsync(FuzzRunOptions request, CancellationToken ct,
        Action<string>? onRunDirectory = null, Action<string>? pathGuard = null) =>
        FuzzRunCampaign.RunAsync(request, ct, onRunDirectory, pathGuard);

    public static Task<int> FuzzTriageAsync(FuzzTriageOptions request, CancellationToken ct,
        Action<string>? pathGuard = null) =>
        FuzzRunCampaign.TriageAsync(request.RunDirectory, ct, pathGuard);

    public static Task<int> FuzzMergeAsync(FuzzMergeOptions request, CancellationToken ct,
        Action<string>? pathGuard = null) =>
        FuzzRunCampaign.MergeAsync(request.RunDirectory, request.OutputDirectory, request.Coverage, ct, pathGuard);
}