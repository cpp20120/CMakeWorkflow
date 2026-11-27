using System.Text.Json;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.CMake;
using CMakeWorkflow.Contracts.History;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Reporting;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Host.Toolchains;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Storage.Documents;
using CMakeWorkflow.Profiling.Analysis;
using CMakeWorkflow.Profiling.Workflows;

namespace CMakeWorkflow.Commands;

internal static class WorkflowCliCommand
{
    internal static Task<int> ExecuteAsync(string[] args, CancellationToken ct) => args switch
    {
        ["reconcile", .. string[] tail] => WorkflowReconcileCommand.ExecuteAsync(tail, ct),
        ["binary-info", string workspace, string operation] => ReadBinaryAsync(workspace, operation, export: false, ct),
        ["binary-export-json", string workspace, string operation] => ReadBinaryAsync(workspace, operation, export: true, ct),
        ["init-binary", string workspace] => InitializeBinaryAsync(workspace),
        ["pack-binary", string json, string frame] => PackBinaryAsync(json, frame, ct),
        ["run-binary", string workspace, string frame, string sha, string id, string results] =>
            RunBinaryAsync(workspace, frame, sha, id, results, ct),
        ["archive", string workspace, string operation, string store] => ArchiveAsync(workspace, operation, store, ct),
        ["generate", .. string[] tail] => WorkflowRecipeGenerateCommand.ExecuteAsync(tail, ct),
        ["plan", .. string[] tail] => WorkflowCommand.ExecuteAsync("plan", tail, ct,
            new ProfileWorkflowExecutor(), toolchainProvider: new WorkflowToolchainProvider()),
        ["run", .. string[] tail] => RunAsync(tail, ct),
        ["resume", .. string[] tail] => ResumeAsync(tail, ct),
        ["report", string directory] => WorkflowReportCommand.ExecuteAsync(directory, ct),
        ["report", string directory, "--json"] => WorkflowReportCommand.ExecuteAsync(directory, ct, json: true),
        ["report", string directory, "--html"] => WorkflowHtmlReport.ExecuteAsync(directory, ct),
        ["sequence", .. string[] tail] => TwoTargetWorkflow.ExecuteAsync(tail, ct),
        _ => Task.FromResult(CliUsage.Write())
    };

    private static async Task<int> ReadBinaryAsync(string workspace, string operation, bool export, CancellationToken ct)
    {
        using DocumentSession input = await BinaryWorkflowSubmission.OpenAcceptedDocumentAsync(Path.GetFullPath(workspace), operation, ct);
        if (export)
        {
            using Stream stdout = Console.OpenStandardOutput();
            await stdout.WriteAsync(input.ExportJsonProjection(), ct);
        }
        else
        {
            DocumentDescriptor descriptor = input.Descriptor;
            await Console.Out.WriteLineAsync($"document={descriptor.Id} blob={descriptor.Blob} canonical={descriptor.Canonical} schema={descriptor.SchemaVersion}");
        }

        return 0;
    }

    private static Task<int> InitializeBinaryAsync(string workspace)
    {
        BinaryWorkflowSubmission.InitializeWorkspace(Path.GetFullPath(workspace));
        return Task.FromResult(0);
    }

    private static async Task<int> PackBinaryAsync(string json, string frame, CancellationToken ct)
    {
        await using FileStream input = new(json, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > DocumentStore.MaximumStoredDocumentBytes)
        {
            throw new InvalidDataException("Workflow document exceeds the size limit.");
        }

        WorkflowDocument workflow = await JsonSerializer.DeserializeAsync(
            input, WorkflowDefinitionJson.Default.WorkflowDocument, ct)
            ?? throw new InvalidDataException("Workflow document is empty.");
        await Console.Out.WriteLineAsync(await PublishBinaryFrameAsync(workflow, frame, ct));
        return 0;
    }

    internal static async Task<string> PublishBinaryFrameAsync(WorkflowDocument workflow, string frame, CancellationToken ct)
    {
        byte[] segment = CapnpWire.Canonicalize(ProductionWorkflowCodec.Encode(workflow));
        byte[] bytes = CapnpWire.Frame(segment);
        string destination = Path.GetFullPath(frame);
        string stage = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (FileStream output = new(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await output.WriteAsync(bytes, ct);
                await output.FlushAsync(ct);
                RandomAccess.FlushToDisk(output.SafeFileHandle);
            }

            ct.ThrowIfCancellationRequested();
            DurableByteWriter.PublishNewFile(stage, destination);
        }
        finally
        {
            File.Delete(stage);
        }

        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static async Task<int> RunBinaryAsync(string workspace, string frame, string sha, string requestId,
        string results, CancellationToken ct)
    {
        string root = Path.GetFullPath(workspace);
        using DurableExecutionCoordinator coordinator = new(ct);
        OperationReference accepted = await BinaryWorkflowSubmission.StartAsync(coordinator, root,
            new(BinaryWorkflowSubmission.Domain, requestId, Path.GetFullPath(frame), sha, 1, Path.GetFullPath(results)), ct);
        await Console.Out.WriteLineAsync(accepted.OperationId);
        await coordinator.DrainAsync();
        using DurableAcceptanceStore ledger = new(root, readOnly: true);
        return ledger.Get(accepted.OperationId)?.State switch { "succeeded" => 0, "canceled" => 130, _ => 1 };
    }

    private static async Task<int> ArchiveAsync(string workspace, string operation, string store, CancellationToken ct)
    {
        MigrationResult result = await WorkflowArchive.ArchiveAcceptedAsync(Path.GetFullPath(workspace), operation, store, ct);
        await Console.Out.WriteLineAsync($"workflowDocument={result.Binary.Id} legacyDocument={result.Legacy.Id} legacySha256={result.Legacy.Legacy}");
        return 0;
    }

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
            new ProfileWorkflowExecutor(), toolchainProvider: new WorkflowToolchainProvider());
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
            }, toolchainProvider: new WorkflowToolchainProvider());

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
