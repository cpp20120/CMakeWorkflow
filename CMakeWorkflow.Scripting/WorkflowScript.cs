using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.Scripting;

/// <summary>Exports a workflow to a controller invoking this .NET file-based app.</summary>
public static class WorkflowScript
{
    internal const string ResponseVariable = "CMAKEWORKFLOW_SCRIPT_RESPONSE";
    internal const string PathMode = "manifest-directory";
    private const int MaxResponseBytes = (4 * ((WorkflowManifestPreparation.MaximumBytes + 2) / 3)) + 4096;

    /// <summary>Writes one versioned response on the invocation's dedicated file channel.</summary>
    /// <remarks>Pass script arguments to your own code normally. Standard output and error remain yours.</remarks>
    public static async Task<int> ExportAsync(WorkflowManifest manifest, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string? path = Environment.GetEnvironmentVariable(ResponseVariable);
        if (path is null || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidOperationException("Run this application through CMakeWorkflow script to export a manifest.");
        }

        byte[] bytes = manifest.CopyBytes();
        if (bytes.Length > WorkflowManifestPreparation.MaximumBytes)
        {
            throw new InvalidDataException("The script manifest exceeds 1 MiB.");
        }
        await using FileStream output = new(path, FileMode.Open, FileAccess.Write, FileShare.Read,
            4096, FileOptions.Asynchronous);
        if (output.Length != 0)
        {
            throw new InvalidOperationException("This invocation already has a script response.");
        }
        await JsonSerializer.SerializeAsync(output, new ScriptExport(1, PathMode, manifest.Sha256, bytes),
            ScriptExportJson.Default.ScriptExport, ct);
        return 0;
    }

    internal static async Task<WorkflowManifest> ReadAsync(FileStream stream, CancellationToken ct)
    {
        long size = stream.Length;
        if (size == 0)
        {
            throw new InvalidDataException("The script did not write a response.");
        }
        if (size > MaxResponseBytes)
        {
            throw new InvalidDataException("The script response size is invalid.");
        }

        byte[] response = new byte[size];
        await stream.ReadExactlyAsync(response, ct);
        if (stream.ReadByte() != -1)
        {
            throw new InvalidDataException("The script response size changed during reading.");
        }

        ScriptExport? export = JsonSerializer.Deserialize(response, ScriptExportJson.Default.ScriptExport);
        if (export is not
            {
                Version: 1,
                RelativePaths: PathMode,
                ManifestBytes.Length: > 0 and <= WorkflowManifestPreparation.MaximumBytes,
                Sha256.Length: 64
            })
        {
            throw new InvalidDataException("Invalid script response version, path mode or manifest payload.");
        }
        if (Convert.ToHexStringLower(SHA256.HashData(export.ManifestBytes)) != export.Sha256)
        {
            throw new InvalidDataException("The exported manifest SHA-256 does not match its bytes.");
        }
        WorkflowManifest manifest = new("script", export.ManifestBytes);
        return manifest.ToDefinition() is { SchemaVersion: 1 or 2, Contexts: not null, Nodes.Count: > 0 }
            ? manifest : throw new InvalidDataException("The response does not contain a standard workflow manifest.");
    }
}

internal sealed record ScriptExport(int Version, string RelativePaths, string Sha256, byte[] ManifestBytes);

[JsonSerializable(typeof(ScriptExport))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
internal sealed partial class ScriptExportJson : JsonSerializerContext;
