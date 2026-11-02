using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    public Task<BenchmarkPreparation> BenchmarkPrepareAsync(BenchmarkPrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            if (request.RequestId is not { Length: 32 } || !request.RequestId.All(Uri.IsHexDigit))
                throw new ApplicationRefusal("invalid_argument", "RequestId must be 32 hexadecimal characters.");
            if (request.Recipe is null)
                throw new ArgumentException("Benchmark recipe is required.");
            if (string.IsNullOrWhiteSpace(request.Recipe.BuildDirectory))
                throw new ArgumentException("Benchmark buildDirectory is required.");
            var manifest = paths.Require(request.OutputPath);
            var build = paths.Require(request.Recipe.BuildDirectory);
            var recipe = request.Recipe with { BuildDirectory = build };
            var bytes = WorkflowManifestBytes.Serialize(BenchmarkWorkflowBuilder.Build(recipe));
            var prepared = new BenchmarkPreparation(1, manifest,
                Convert.ToHexString(SHA256.HashData(bytes)));
            var bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
                ".cmakeworkflow", "benchmark-preparations"));
            Directory.CreateDirectory(bindingDirectory);
            var binding = paths.Require(Path.Combine(bindingDirectory,
                request.RequestId.ToLowerInvariant() + ".json"));
            await CommitOnceAsync(binding,
                JsonSerializer.SerializeToUtf8Bytes(prepared, HostJson.Default.BenchmarkPreparation), ct);
            Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
            await CommitOnceAsync(manifest, bytes, ct);
            return prepared;
        }, cancellationToken);
}
