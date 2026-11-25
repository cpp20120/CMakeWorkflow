using System.Security.Cryptography;
using System.Text.Json;
using CMakeWorkflow.Client.Workflows;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Scripting;

/// <summary>A detached snapshot of exact workflow manifest bytes and their SHA-256.</summary>
public sealed class WorkflowManifest
{
    private readonly byte[] bytes;

    internal WorkflowManifest(string name, byte[] bytes)
    {
        Name = name;
        this.bytes = bytes;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    /// <summary>The authoring label. It is not part of the wire manifest or its digest.</summary>
    public string Name { get; }

    public string Sha256 { get; }
    public string FileName => ".cmwf-workflow-" + Sha256 + ".json";

    public byte[] CopyBytes() => (byte[])bytes.Clone();

    public WorkflowDocument ToDefinition() =>
        JsonSerializer.Deserialize(bytes, WorkflowDefinitionJson.Default.WorkflowDocument)!;

    /// <summary>Publishes the exact bytes in an existing local directory and returns the local file path.</summary>
    /// <remarks>
    /// The Host resolves relative workflow paths from the directory containing the staged manifest.
    /// Choose the local directory explicitly. Save neither stages remote files nor starts an operation.
    /// </remarks>
    public async Task<string> SaveAsync(string absoluteDirectory, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteDirectory);
        if (!Path.IsPathFullyQualified(absoluteDirectory))
        {
            throw new ArgumentException("Provide an absolute local manifest base directory.", nameof(absoluteDirectory));
        }

        string path = Path.Combine(Path.GetFullPath(absoluteDirectory), FileName);
        ct.ThrowIfCancellationRequested();
        if (!File.Exists(path))
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 8192, FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await stream.WriteAsync(bytes, ct);
                    await stream.FlushAsync(ct);
                }
                ct.ThrowIfCancellationRequested();
                try
                {
                    File.Move(temporary, path);
                }
                catch (IOException) when (File.Exists(path))
                {
                    // A concurrent publisher won. Check its exact bytes below.
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }

        FileInfo info = new(path);
        if (info.LinkTarget is not null || (info.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException("A saved workflow manifest must be a regular file.");
        }
        await using FileStream saved = File.OpenRead(path);
        return saved.Length == bytes.Length && Convert.ToHexStringLower(await SHA256.HashDataAsync(saved, ct)) == Sha256
            ? path : throw new InvalidDataException("The existing workflow manifest differs from the authored bytes.");
    }

    /// <summary>Publishes these exact bytes in an existing directory on the connected Host.</summary>
    public Task<PreparedWorkflow> PrepareAsync(WorkflowService workflows, string hostResultsDirectory,
        string manifestBase = ".", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(workflows);
        return workflows.PrepareManifestAsync(bytes, Sha256, hostResultsDirectory, manifestBase, ct);
    }

    /// <summary>Creates a preflight request for explicitly staged Host-side paths.</summary>
    /// <remarks>The Host validates paths and bytes. No controller path translation or file transfer is implied.</remarks>
    public WorkflowPreflightOptions AtHost(string hostManifest, string hostResultsDirectory, int jobs = 1)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(hostManifest);
        ArgumentException.ThrowIfNullOrWhiteSpace(hostResultsDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(jobs, 1);
        return new(hostManifest, Sha256, jobs, hostResultsDirectory);
    }
}
