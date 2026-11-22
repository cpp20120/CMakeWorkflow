using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Sources;
#endif

internal static class SnapshotIdentity
{
    internal const int SchemaVersion = 1;

    internal static string GetId(IReadOnlyList<SnapshotFile> files) =>
        Convert.ToHexString(SHA256.HashData(Serialize(files)));

    internal static byte[] Serialize(IReadOnlyList<SnapshotFile> files) =>
        JsonSerializer.SerializeToUtf8Bytes(new SnapshotManifest(SchemaVersion, files),
            SnapshotEvidenceJson.Default.SnapshotManifest);
}

[JsonSerializable(typeof(SnapshotManifest))]
internal sealed partial class SnapshotEvidenceJson : JsonSerializerContext;
