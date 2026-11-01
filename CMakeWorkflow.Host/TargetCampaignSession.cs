using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Campaigns;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    // Target build/invoke/run/sequence use the SAME durable DAG admission as
    // workflow/start; this operation merely prepares an immutable manifest.
    public Task<TargetPreparation> TargetPrepareAsync(TargetPrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (request.RequestId is not { Length: 32 } || !request.RequestId.All(Uri.IsHexDigit))
                throw new ApplicationRefusal("invalid_argument", "RequestId must be a 32-digit hex identifier.");
            var output = paths.Require(request.ResultsDirectory);
            var build = paths.Require(request.BuildDirectory);
            var workflow = TargetWorkflowBuilder.Build(request.Action, build, request.Target,
                request.SecondTarget, request.Configuration, request.Arguments, request.SecondArguments,
                request.Toolchain);
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument) + "\n");
            var hash = Convert.ToHexString(SHA256.HashData(bytes));
            var manifest = paths.Require(Path.Combine(output,
                "target-workflow-" + request.RequestId.ToLowerInvariant() + ".json"));
            var bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
                ".cmakeworkflow", "target-preparations"));
            Directory.CreateDirectory(bindingDirectory);
            var binding = paths.Require(Path.Combine(bindingDirectory,
                request.RequestId.ToLowerInvariant() + ".json"));
            var prepared = new TargetPreparation(1, manifest, hash, output);
            await CommitOnceAsync(binding,
                JsonSerializer.SerializeToUtf8Bytes(prepared, HostJson.Default.TargetPreparation), ct);
            Directory.CreateDirectory(output);
            await CommitOnceAsync(manifest, bytes, ct);
            return prepared;
        }, cancellationToken);

    // These calls are connection-scoped. They never return a durable operation
    // handle and are never restarted by Host after disconnect or a lost reply.
    public Task<ProfileReplayResponse> ProfileReplayAsync(ProfileReplayRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var result = await ProfileReplayCommand.ReplayAsync(new ProfileReplayOptions(
                paths.Require(request.BaselineDirectory), paths.Require(request.CandidateDirectory),
                request.NodeId, request.CaseName, request.HarnessIndex, request.Backend,
                paths.Require(request.ResultsDirectory), request.TimeoutSeconds), ct,
                pathGuard: path => { paths.Require(path); });
            return new ProfileReplayResponse(result.SchemaVersion,
                paths.Require(result.RunDirectory), paths.Require(result.AnalysisFile));
        }, cancellationToken);

    private static CampaignExecutionResult Result(string kind, int code, string? directory) =>
        new(1, kind, code == 0 ? "succeeded" : "failed", code, directory);

    public Task<CampaignExecutionResult> CampaignMutationAsync(CampaignMutationRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var source = paths.Require(request.SourceDirectory);
            var report = paths.Require(request.ReportPath ?? Path.Combine(source,
                request.Suite == "queue" ? "out/queue-mutations/report.json" : "out/protocol-mutations/report.json"));
            paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json"));
            paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json.tmp"));
            var code = await CampaignService.MutationAsync(new MutationOptions(source, request.Suite,
                request.Compiler, request.Seed, request.Cases, request.Steps, request.TimeoutSeconds, report), ct);
            return Result("mutation", code, Path.GetDirectoryName(report));
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignWeakMemoryAsync(CampaignWeakMemoryRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var source = paths.Require(request.SourceDirectory);
            var relacy = paths.Require(request.RelacyDirectory);
            var report = paths.Require(request.ReportPath ?? Path.Combine(source, "out/weak-memory/report.json"));
            paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json"));
            paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json.tmp"));
            var code = await CampaignService.WeakMemoryAsync(new WeakMemoryOptions(source,
                request.Protocol, relacy, request.Compiler, request.TimeoutSeconds, report), ct);
            return Result("weak-memory", code, Path.GetDirectoryName(report));
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzSeedAsync(CampaignFuzzSeedRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Layer) || request.Layer.IndexOfAny(['/', '\\', '.', ':']) >= 0)
                throw new ArgumentException("Invalid fuzz layer.");
            var output = paths.Require(request.OutputDirectory);
            var directory = paths.Require(Path.Combine(output, request.Layer));
            var code = await CampaignService.FuzzSeedAsync(new(request.Layer, output), ct);
            return Result("fuzz-seed", code, directory);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzRunAsync(CampaignFuzzRunRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var build = paths.Require(request.BuildDirectory);
            var output = paths.Require(request.ResultsDirectory);
            var corpus = request.CorpusDirectory is null ? null : paths.Require(request.CorpusDirectory);
            string? run = null;
            var code = await CampaignService.FuzzRunAsync(new FuzzRunOptions(build, request.Layer,
                request.Backend, corpus, output, request.Configuration, request.DurationSeconds,
                request.InputTimeoutSeconds, request.MaxLength, request.ReplayOnly), ct,
                directory => run = paths.Require(directory),
                path => { paths.Require(path); });
            return Result("fuzz-run", code, run);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzTriageAsync(CampaignFuzzTriageRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var directory = paths.Require(request.RunDirectory);
            var code = await CampaignService.FuzzTriageAsync(new(directory), ct, path => { paths.Require(path); });
            return Result("fuzz-triage", code, directory);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzMergeAsync(CampaignFuzzMergeRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            var directory = paths.Require(request.RunDirectory);
            var output = request.OutputDirectory is null ? null : paths.Require(request.OutputDirectory);
            var code = await CampaignService.FuzzMergeAsync(new(directory, output, request.Coverage), ct,
                path => { paths.Require(path); });
            return Result("fuzz-merge", code, directory);
        }, cancellationToken);
}
