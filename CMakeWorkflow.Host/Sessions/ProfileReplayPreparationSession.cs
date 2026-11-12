using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Profiling;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Profiling.Replay;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    public Task<ProfilePreparation> ProfileReplayPrepareAsync(ProfileReplayPrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (request.RequestId is not { Length: 32 } || !request.RequestId.All(Uri.IsHexDigit))
            {
                throw new ApplicationRefusal("invalid_argument", "RequestId must be a 32-digit hex identifier.");
            }

            string directory = paths.Require(request.ResultsDirectory);
            string baseline = paths.Require(request.BaselineDirectory);
            string candidate = paths.Require(request.CandidateDirectory);
            WorkflowDocument workflow = await ProfileReplayCommand.PrepareWorkflowAsync(
                new ProfileReplayOptions(baseline, candidate, request.NodeId, request.CaseName,
                    request.HarnessIndex, request.Backend, directory, request.TimeoutSeconds),
                path => { _ = paths.Require(path); }, ct);
            byte[] bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument) + "\n");
            string hash = Convert.ToHexString(SHA256.HashData(bytes));
            string id = CanonicalRequestId(request.RequestId);
            string manifest = paths.Require(Path.Combine(directory, "profile-replay-workflow-" + id + ".json"));
            string bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
                ".cmakeworkflow", "profile-replay-preparations"));
            _ = Directory.CreateDirectory(bindingDirectory);
            string binding = paths.Require(Path.Combine(bindingDirectory, id + ".json"));
            ProfilePreparation prepared = new(1, manifest, hash, directory);
            await CommitOnceAsync(binding,
                JsonSerializer.SerializeToUtf8Bytes(prepared, HostJson.Default.ProfilePreparation), ct);
            _ = Directory.CreateDirectory(directory);
            await CommitOnceAsync(manifest, bytes, ct);
            return prepared;
        }, cancellationToken);
}
