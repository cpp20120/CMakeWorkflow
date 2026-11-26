using System.Text.Json;
using CMakeWorkflow.Campaigns;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.History;
using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;

namespace CMakeWorkflow.Commands;

/// <summary>Routes CLI arguments to command handlers without owning execution or cancellation lifetime.</summary>
internal static class CliApplication
{
    internal static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => args switch
    {
        ["kit", .. string[] tail] => KitCommand.ExecuteAsync(tail, ct),
        ["format" or "lint" or "analyze", .. string[] tail] => ClangCommand.ExecuteAsync(args[0], tail, ct),
        ["project", .. string[] tail] => ProjectSdkCommand.ExecuteAsync(tail, ct),
        ["experiment", .. string[] tail] => ExperimentCommand.ExecuteAsync(tail, ct),
        ["script", .. string[] tail] => ScriptCommand.ExecuteAsync(tail, ct),
        ["remote", .. string[] tail] => RemoteCommand.ExecuteAsync(tail, ct),
        ["cas", .. string[] tail] => ArtifactCasCommand.ExecuteAsync(tail, ct),
        ["host", .. string[] tail] => HostCommand.ExecuteAsync(tail, ct),
        ["history", .. string[] tail, "--html", string output] => HistoryHtmlAsync(tail, output, ct),
        ["history", .. string[] tail] => HistoryCommand.ExecuteAsync(tail, ct),
        ["env", .. string[] tail] => EnvironmentCommand.ExecuteAsync(tail, ct),
        ["tooling", .. string[] tail] => ToolingCommand.ExecuteAsync(tail, ct),
        ["setup", .. string[] tail] => ToolingCommand.ExecuteAsync(["setup", .. tail], ct),
        ["dagflow", "profile", .. string[] tail] => ProfileCliCommand.ExecuteAsync(tail, ct),
        ["profile", string subcommand, .. string[] tail] when !subcommand.StartsWith('-') && subcommand != "help" =>
            ProfileCliCommand.ExecuteAsync([subcommand, .. tail], ct),
        ["new" or "restore" or "inspect" or "doctor" or "configure" or "build" or "test" or "run" or "benchmark" or "profile" or "pack", .. string[] tail] =>
            DeveloperCommand.ExecuteAsync(args[0], tail, ct),
        ["dagflow", .. string[] tail] => CampaignCommand.ExecuteAsync(tail, ct),
        ["mutation" or "weak-memory" or "fuzz", ..] => CampaignCommand.ExecuteAsync(args, ct),
        ["context", "inspect", string directory] => InspectContextAsync(directory, ct),
        ["context", "inspect", string directory, "--json"] => InspectContextAsync(directory, ct),
        ["workflow", .. string[] tail] => WorkflowCliCommand.ExecuteAsync(tail, ct),
        ["target", .. string[] tail] => TargetCommand.ExecuteAsync(tail, ct),
        _ => Task.FromResult(CliUsage.Write())
    };

    private static Task<int> HistoryHtmlAsync(string[] args, string output, CancellationToken ct) =>
        string.Equals(Path.GetExtension(output), ".html", StringComparison.OrdinalIgnoreCase)
        && !output.StartsWith("--", StringComparison.Ordinal)
            ? HistoryCommand.ExecuteAsync(args, data => WorkflowHtmlReport.WriteAsync(output, data, ct), ct)
            : throw new ArgumentException("history --html requires an output path ending in .html.");

    private static async Task<int> InspectContextAsync(string directory, CancellationToken ct)
    {
        ObservedBuildContext observed = await CMakeInspection.InspectAsync(BuildTreeOwnership.CanonicalPath(directory), ct);
        Console.WriteLine(JsonSerializer.Serialize(observed, WorkflowJson.Default.ObservedBuildContext));
        return 0;
    }
}
