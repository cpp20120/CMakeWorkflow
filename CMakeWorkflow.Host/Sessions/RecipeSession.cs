using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.CMake.Recipes;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.Contracts.Recipes;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Recipes;
using CMakeWorkflow.Profiling.Workflows;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    private static void ValidateRecipeRequest(string requestId)
    {
        if (requestId is not { Length: 32 } || !requestId.All(Uri.IsHexDigit))
        {
            throw new ApplicationRefusal("invalid_argument", "RequestId must be 32 hexadecimal characters.");
        }
    }

    /// <summary>Commits a preparation binding before its immutable manifest.</summary>
    /// <remarks>
    /// Caller has validated recipe and workspace paths. Commit-once writes accept identical bytes and refuse conflicts.
    /// Cancellation can leave only the binding committed; an explicit identical retry finishes the manifest write.
    /// No durable workflow admission, build or dispatch occurs here; committed files remain caller-owned recovery evidence.
    /// </remarks>
    private static async Task CommitRecipeAsync(WorkspaceReadPaths paths, string kind,
        string requestId, string manifest, WorkflowDocument document,
        byte[] bindingBytes, CancellationToken ct)
    {
        // The existing commit-once writer persists the request binding before
        // the manifest. A retry can finish an interrupted write without changing
        // an already committed file. workflow/start remains a separate admission.
        string bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
            ".cmakeworkflow", kind + "-preparations"));
        _ = Directory.CreateDirectory(bindingDirectory);

#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
        string binding = paths.Require(Path.Combine(bindingDirectory,
                    requestId.ToLowerInvariant() + ".json"));
#pragma warning restore CA1308

        await CommitOnceAsync(binding, bindingBytes, ct);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(manifest)!);
        await CommitOnceAsync(manifest, WorkflowManifestBytes.Serialize(document), ct);
    }

    private static string Hash(WorkflowDocument document) =>
        Convert.ToHexString(SHA256.HashData(WorkflowManifestBytes.Serialize(document)));

    public Task<ProfileRecipePreparation> ProfileRecipePrepareAsync(ProfileRecipePrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
    {
        ValidateRecipeRequest(request.RequestId);
        if (request.Recipe?.Variants is null || request.Recipe.Variants.Any(v => v is null))
        {
            throw new ArgumentException("Profile recipe variants are required.");
        }

        string manifest = paths.Require(request.OutputPath);
        ProfileRecipeV1 recipe = request.Recipe with
        {
            Variants = [.. request.Recipe.Variants.Select(v => v with
            {
                BuildDirectory = paths.Require(v.BuildDirectory)
            })],
            SourceDirectory = request.Recipe.SourceDirectory is null
                ? null
                : paths.Require(request.Recipe.SourceDirectory)
        };
        WorkflowDocument document = ProfileRecipeBuilder.Build(recipe);
        ProfileRecipePreparation prepared = new(1, manifest, Hash(document));
        await CommitRecipeAsync(paths, "profile-recipe", request.RequestId, manifest,
            document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                HostJson.Default.ProfileRecipePreparation), ct);
        return prepared;
    }, cancellationToken);

    public Task<TestMatrixPreparation> TestMatrixPrepareAsync(TestMatrixPrepareRequest request,
        CancellationToken cancellationToken) => ApplicationCallAsync(request, async (paths, ct) =>
    {
        ValidateRecipeRequest(request.RequestId);
        if (request.Recipe is null)
        {
            throw new ArgumentException("Test Matrix recipe is required.");
        }

        string manifest = paths.Require(request.OutputPath);
        TestMatrixRecipeV1 recipe = request.Recipe with
        {
            BuildDirectory = paths.Require(request.Recipe.BuildDirectory)
        };
        WorkflowDocument document = TestMatrixWorkflowBuilder.Build(recipe);
        TestMatrixPreparation prepared = new(1, manifest, Hash(document));
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
        {
            throw new ArgumentException("Build Matrix variants are required.");
        }

        string manifest = paths.Require(request.OutputPath);
        BuildMatrixRecipeV1 recipe = request.Recipe with
        {
            Variants = [.. request.Recipe.Variants.Select(v =>
                v with { BuildDirectory = paths.Require(v.BuildDirectory) })]
        };
        WorkflowDocument document = BuildMatrixWorkflowBuilder.Build(recipe);
        BuildMatrixPreparation prepared = new(1, manifest, Hash(document));
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
        {
            throw new ArgumentException("Benchmark Matrix variants are required.");
        }

        string manifest = paths.Require(request.OutputPath);
        BenchmarkMatrixRecipeV1 recipe = request.Recipe with
        {
            Variants = [.. request.Recipe.Variants.Select(v =>
                v with { BuildDirectory = paths.Require(v.BuildDirectory) })]
        };
        WorkflowDocument document = BenchmarkMatrixWorkflowBuilder.Build(recipe);
        BenchmarkMatrixPreparation prepared = new(1, manifest, Hash(document));
        await CommitRecipeAsync(paths, "benchmark-matrix", request.RequestId, manifest,
            document, JsonSerializer.SerializeToUtf8Bytes(prepared,
                HostJson.Default.BenchmarkMatrixPreparation), ct);
        return prepared;
    }, cancellationToken);
}