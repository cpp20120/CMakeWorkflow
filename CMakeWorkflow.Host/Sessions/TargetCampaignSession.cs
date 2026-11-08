using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Campaigns;
using CMakeWorkflow.Campaigns.FuzzCorpus;
using CMakeWorkflow.Campaigns.Mutation;
using CMakeWorkflow.Campaigns.WeakMemory;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Campaigns;
using CMakeWorkflow.Host.Profiling;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Workflows;
using CMakeWorkflow.Profiling.Replay;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    // Target build/invoke/run/sequence use the SAME durable DAG admission as
    // workflow/start; this operation merely prepares an immutable manifest.
    public Task<TargetPreparation> TargetPrepareAsync(TargetPrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (request.RequestId is not { Length: 32 } || !request.RequestId.All(Uri.IsHexDigit))
            {
                throw new ApplicationRefusal("invalid_argument", "RequestId must be a 32-digit hex identifier.");
            }

            string output = paths.Require(request.ResultsDirectory);
            string build = paths.Require(request.BuildDirectory);
            WorkflowDocument workflow = TargetWorkflowBuilder.Build(request.Action, build, request.Target,
                request.SecondTarget, request.Configuration, request.Arguments, request.SecondArguments,
                request.Toolchain);
            byte[] bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument) + "\n");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));

            string manifest = paths.Require(Path.Combine(output,
                            "target-workflow-" + CanonicalRequestId(request.RequestId) + ".json"));

            string bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
                ".cmakeworkflow", "target-preparations"));
            _ = Directory.CreateDirectory(bindingDirectory);

            string binding = paths.Require(Path.Combine(bindingDirectory,
                            CanonicalRequestId(request.RequestId) + ".json"));

            TargetPreparation prepared = new(1, manifest, hash, output);
            await CommitOnceAsync(binding,
                JsonSerializer.SerializeToUtf8Bytes(prepared, HostJson.Default.TargetPreparation), ct);
            _ = Directory.CreateDirectory(output);
            await CommitOnceAsync(manifest, bytes, ct);
            return prepared;
        }, cancellationToken);

    // These calls are connection-scoped. They never return a durable operation
    // handle and are never restarted by Host after disconnect or a lost reply.
    public Task<ProfileReplayResponse> ProfileReplayAsync(ProfileReplayRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            ProfileReplayOutcome result = await ProfileReplayCommand.ReplayAsync(new ProfileReplayOptions(
                    paths.Require(request.BaselineDirectory), paths.Require(request.CandidateDirectory),
                    request.NodeId, request.CaseName, request.HarnessIndex, request.Backend,
                    paths.Require(request.ResultsDirectory), request.TimeoutSeconds), ct,
                pathGuard: path => { _ = paths.Require(path); });
            return new ProfileReplayResponse(result.SchemaVersion,
                paths.Require(result.RunDirectory), paths.Require(result.AnalysisFile));
        }, cancellationToken);

    private static CampaignExecutionResult Result(string kind, int code, string? directory) =>
        new(1, kind, code == 0 ? "succeeded" : "failed", code, directory);

    public Task<CampaignExecutionResult> CampaignMutationAsync(CampaignMutationRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string source = paths.Require(request.SourceDirectory);
            string report = paths.Require(request.ReportPath ?? Path.Combine(source,
                request.Suite == "queue" ? "out/queue-mutations/report.json" : "out/protocol-mutations/report.json"));
            _ = paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json"));
            _ = paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json.tmp"));
            int code = await CampaignService.MutationAsync(new MutationOptions(source, request.Suite,
                request.Compiler, request.Seed, request.Cases, request.Steps, request.TimeoutSeconds, report), ct);
            return Result("mutation", code, Path.GetDirectoryName(report));
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignWeakMemoryAsync(CampaignWeakMemoryRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string source = paths.Require(request.SourceDirectory);
            string relacy = paths.Require(request.RelacyDirectory);
            string report = paths.Require(request.ReportPath ?? Path.Combine(source, "out/weak-memory/report.json"));
            _ = paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json"));
            _ = paths.Require(Path.Combine(Path.GetDirectoryName(report)!, "campaign-completion.json.tmp"));
            int code = await CampaignService.WeakMemoryAsync(new WeakMemoryOptions(source,
                request.Protocol, relacy, request.Compiler, request.TimeoutSeconds, report), ct);
            return Result("weak-memory", code, Path.GetDirectoryName(report));
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzSeedAsync(CampaignFuzzSeedRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Layer) || request.Layer.IndexOfAny(['/', '\\', '.', ':']) >= 0)
            {
                throw new ArgumentException("Invalid fuzz layer.");
            }

            string output = paths.Require(request.OutputDirectory);
            string directory = paths.Require(Path.Combine(output, request.Layer));
            int code = await CampaignService.FuzzSeedAsync(new(request.Layer, output), ct);
            return Result("fuzz-seed", code, directory);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzRunAsync(CampaignFuzzRunRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string build = paths.Require(request.BuildDirectory);
            string output = paths.Require(request.ResultsDirectory);
            string? corpus = request.CorpusDirectory is null ? null : paths.Require(request.CorpusDirectory);
            string? run = null;
            int code = await CampaignService.FuzzRunAsync(new FuzzRunOptions(build, request.Layer,
                    request.Backend, corpus, output, request.Configuration, request.DurationSeconds,
                    request.InputTimeoutSeconds, request.MaxLength, request.ReplayOnly), ct,
                directory => run = paths.Require(directory),
                path => { _ = paths.Require(path); });
            return Result("fuzz-run", code, run);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzTriageAsync(CampaignFuzzTriageRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string directory = paths.Require(request.RunDirectory);
            int code = await CampaignService.FuzzTriageAsync(new(directory), ct, path => { _ = paths.Require(path); });
            return Result("fuzz-triage", code, directory);
        }, cancellationToken);

    public Task<CampaignExecutionResult> CampaignFuzzMergeAsync(CampaignFuzzMergeRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string directory = paths.Require(request.RunDirectory);
            string? output = request.OutputDirectory is null ? null : paths.Require(request.OutputDirectory);
            int code = await CampaignService.FuzzMergeAsync(new(directory, output, request.Coverage), ct,
                path => { _ = paths.Require(path); });
            return Result("fuzz-merge", code, directory);
        }, cancellationToken);
}
