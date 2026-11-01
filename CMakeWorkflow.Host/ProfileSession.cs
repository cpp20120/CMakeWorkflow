using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.Profiling;

namespace CMakeWorkflow.Host;

public sealed partial class DoctorSession
{
    // Preparing a profile is a metadata-only operation. It never configures,
    // builds or starts an executable. workflow/start commits its immutable
    // manifest with the existing durable exactly-one-admission protocol.
    public Task<ProfilePreparation> ProfilePrepareAsync(ProfilePrepareRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, (paths, ct) => PrepareAsync(paths, request, ct), cancellationToken);

    private static async Task<ProfilePreparation> PrepareAsync(WorkspaceReadPaths paths,
        ProfilePrepareRequest request, CancellationToken cancellationToken)
    {
        if (request.RequestId is not { Length: 32 }
            || !request.RequestId.All(Uri.IsHexDigit))
            throw new ApplicationRefusal("invalid_argument", "RequestId must be 32 hexadecimal characters.");
        if (request.Variants is not { Count: > 0 and <= 32 })
            throw new ApplicationRefusal("invalid_argument", "Profile requires 1..32 variants.");
        if (request.Variants.Any(v => v is null) || request.Arguments?.Any(a => a is null) == true)
            throw new ApplicationRefusal("invalid_argument", "Profile variants and arguments cannot contain null entries.");

        var output = paths.Require(request.ResultsDirectory);
        var manifest = paths.Require(Path.Combine(output,
            "profile-workflow-" + request.RequestId.ToLowerInvariant() + ".json"));
        var variants = request.Variants.Select(v => new ProfileVariant(v.Name,
            paths.Require(v.BuildDirectory))).ToArray();
        var source = request.SourceDirectory is null ? null : paths.Require(request.SourceDirectory);
        if (request.ConfigurePreset is not null && source is null)
            source = paths.WorkspaceDirectory;
        WorkflowDocument workflow;
        try
        {
            workflow = ProfileWorkflowBuilder.Build(variants, request.Target, source,
                request.Configuration, request.ConfigurePreset, request.Repeats,
                request.TimeoutSeconds, request.Arguments ?? [], request.Backend, request.Mode);
            foreach (var variant in request.Variants)
                workflow.Contexts[variant.Name].Toolchain = variant.Toolchain;
        }
        catch (ArgumentException error)
        {
            throw new ApplicationRefusal("invalid_argument", error.Message);
        }

        var bytes = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(workflow, WorkflowJson.Default.WorkflowDocument) + "\n");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        // Bind a request ID across the entire workspace, including the chosen
        // result directory. A retry after a lost response can finish writing
        // the manifest, but a different request cannot claim the same ID.
        var bindingDirectory = paths.Require(Path.Combine(paths.WorkspaceDirectory,
            ".cmakeworkflow", "profile-preparations"));
        Directory.CreateDirectory(bindingDirectory);
        var binding = paths.Require(Path.Combine(bindingDirectory,
            request.RequestId.ToLowerInvariant() + ".json"));
        var preparation = new ProfilePreparation(1, manifest, hash, output);
        var bindingBytes = JsonSerializer.SerializeToUtf8Bytes(preparation, HostJson.Default.ProfilePreparation);
        await CommitOnceAsync(binding, bindingBytes, cancellationToken);
        Directory.CreateDirectory(output);
        await CommitOnceAsync(manifest, bytes, cancellationToken);
        return preparation;
    }

    private static async Task CommitOnceAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var temp = Path.Combine(Path.GetDirectoryName(path)!, ".profile-workflow-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try { File.Move(temp, path); }
            catch (IOException) when (File.Exists(path)) { /* Compare committed contents. */ }
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }

        var actual = await File.ReadAllBytesAsync(path, cancellationToken);
        if (!actual.AsSpan().SequenceEqual(bytes))
            throw new ApplicationRefusal("request_id_conflict",
                "A different profile preparation already exists for this RequestId.");
    }
}
