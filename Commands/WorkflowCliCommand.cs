using System.Text.Json;
using CMakeWorkflow.CMake;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Profiling.Analysis;
using CMakeWorkflow.Profiling.Workflows;

namespace CMakeWorkflow.Commands;

internal static class WorkflowCliCommand
{
    internal static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => args switch
    {
        ["reconcile", .. string[] tail] => WorkflowReconcileCommand.ExecuteAsync(tail, ct),
        ["generate", .. string[] tail] => WorkflowRecipeGenerateCommand.ExecuteAsync(tail, ct),
        ["plan", .. string[] tail] => WorkflowCommand.ExecuteAsync("plan", tail, ct,
            new ProfileWorkflowExecutor(), toolchainProvider: new WindowsWorkflowToolchainProvider()),
        ["run", .. string[] tail] => RunAsync(tail, ct),
        ["resume", .. string[] tail] => ResumeAsync(tail, ct),
        ["report", string directory] => WorkflowReportCommand.ExecuteAsync(directory, ct),
        ["report", string directory, "--json"] => WorkflowReportCommand.ExecuteAsync(directory, ct, json: true),
        ["sequence", .. string[] tail] => TwoTargetWorkflow.ExecuteAsync(tail, ct),
        _ => Task.FromResult(CliUsage.Write())
    };

    private static async Task<int> ResumeAsync(string[] args, CancellationToken ct)
    {
        if (args.Length != 1)
        {
            throw new ArgumentException("Usage: workflow resume <run-directory>");
        }

        string directory = Path.GetFullPath(args[0]);
        WorkflowReport saved = JsonSerializer.Deserialize(await File.ReadAllBytesAsync(
            Path.Combine(directory, "run.json"), ct), WorkflowJson.Default.WorkflowReport)
            ?? throw new InvalidDataException("Invalid run report.");
        return await WorkflowCommand.ExecuteAsync("run", [saved.Workflow, "--resume-dir", directory], ct,
            new ProfileWorkflowExecutor(), toolchainProvider: new WindowsWorkflowToolchainProvider());
    }

    private static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        (string[] commandArgs, HistoryPolicy? historyPolicy) = CliHistory.Parse(args);
        return await CliHistory.RunAndIndexAsync(async onRun =>
        {
            string? runDirectory = null;
            int code = await WorkflowCommand.ExecuteAsync("run", commandArgs, ct, new ProfileWorkflowExecutor(), directory =>
            {
                runDirectory = directory;
                onRun(directory);
            }, toolchainProvider: new WindowsWorkflowToolchainProvider());

            // Analyze captured profile passes before indexing; ordinary workflows never invoke the analyzer.
            if (code == 0 && runDirectory is not null && Directory.EnumerateFiles(runDirectory, "*.results.json").Any())
            {
                code = await ProfileAnalyzer.AnalyzeAsync(runDirectory, ct, emitResultPath: false);
                await Console.Error.WriteLineAsync($"[CMakeWorkflow] Profile analysis: {Path.Combine(runDirectory, "analysis.json")}");
            }

            return code;
        }, historyPolicy);
    }
}
