using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    private static void ValidateRecipeRequest(string requestId)
    {
        if (requestId is not { Length: 32 } || !requestId.All(Uri.IsHexDigit))
            throw new ApplicationRefusal("invalid_argument", "RequestId must be 32 hexadecimal characters.");
    }

    private static async Task CommitRecipeAsync(WorkspaceReadPaths paths, string kind,
        string requestId, string manifest, WorkflowDocument document,
        byte[] bindingBytes, CancellationToken ct)
    {
        // The existing commit-once writer persists the request binding before
        // the manifest. A retry can finish an interrupted write without changing
        // an already committed file. workflow/start remains a separate admission.
        var bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
            ".cmakeworkflow", kind + "-preparations"));
        Directory.CreateDirectory(bindingDirectory);
        var binding = paths.Require(Path.Combine(bindingDirectory,
            requestId.ToLowerInvariant() + ".json"));
        await CommitOnceAsync(binding, bindingBytes, ct);
        Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await CommitOnceAsync(manifest, WorkflowManifestBytes.Serialize(document), ct);
    }

    private static string Hash(WorkflowDocument document) =>
        Convert.ToHexString(SHA256.HashData(WorkflowManifestBytes.Serialize(document)));

    public Task<ProfileRecipePreparation> ProfileRecipePrepareAsync(ProfileRecipePrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
        {
            ValidateRecipeRequest(request.RequestId);
            if (request.Recipe?.Variants is null || request.Recipe.Variants.Any(v => v is null))
                throw new ArgumentException("Profile recipe variants are required.");
            var manifest = paths.Require(request.OutputPath);
            var recipe = request.Recipe with
            {
                Variants = request.Recipe.Variants.Select(v => v with
                { BuildDirectory = paths.Require(v.BuildDirectory) }).ToArray(),
                SourceDirectory = request.Recipe.SourceDirectory is null ? null
                    : paths.Require(request.Recipe.SourceDirectory)
            };
            var document = ProfileRecipeBuilder.Build(recipe);
            var prepared = new ProfileRecipePreparation(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "profile-recipe", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.ProfileRecipePreparation), ct);
            return prepared;
        }, cancellationToken);

    public Task<TestMatrixPreparation> TestMatrixPrepareAsync(TestMatrixPrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
        {
            ValidateRecipeRequest(request.RequestId);
            if (request.Recipe is null) throw new ArgumentException("Test Matrix recipe is required.");
            var manifest = paths.Require(request.OutputPath);
            var recipe = request.Recipe with
            { BuildDirectory = paths.Require(request.Recipe.BuildDirectory) };
            var document = TestMatrixWorkflowBuilder.Build(recipe);
            var prepared = new TestMatrixPreparation(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "test-matrix", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.TestMatrixPreparation), ct);
            return prepared;
        }, cancellationToken);

    public Task<BuildMatrixPreparation> BuildMatrixPrepareAsync(BuildMatrixPrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
        {
            ValidateRecipeRequest(request.RequestId);
            if (request.Recipe?.Variants is null || request.Recipe.Variants.Any(v => v is null))
                throw new ArgumentException("Build Matrix variants are required.");
            var manifest = paths.Require(request.OutputPath);
            var recipe = request.Recipe with { Variants = request.Recipe.Variants.Select(v =>
                v with { BuildDirectory = paths.Require(v.BuildDirectory) }).ToArray() };
            var document = BuildMatrixWorkflowBuilder.Build(recipe);
            var prepared = new BuildMatrixPreparation(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "build-matrix", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.BuildMatrixPreparation), ct);
            return prepared;
        }, cancellationToken);

    public Task<BenchmarkMatrixPreparation> BenchmarkMatrixPrepareAsync(BenchmarkMatrixPrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
        {
            ValidateRecipeRequest(request.RequestId);
            if (request.Recipe?.Variants is null || request.Recipe.Variants.Any(v => v is null))
                throw new ArgumentException("Benchmark Matrix variants are required.");
            var manifest = paths.Require(request.OutputPath);
            var recipe = request.Recipe with { Variants = request.Recipe.Variants.Select(v =>
                v with { BuildDirectory = paths.Require(v.BuildDirectory) }).ToArray() };
            var document = BenchmarkMatrixWorkflowBuilder.Build(recipe);
            var prepared = new BenchmarkMatrixPreparation(1, manifest, Hash(document));
            await CommitRecipeAsync(paths, "benchmark-matrix", request.RequestId, manifest,
                document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                    HostJson.Default.BenchmarkMatrixPreparation), ct);
            return prepared;
        }, cancellationToken);
}
