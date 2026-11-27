using System.Security.Cryptography;
using Capnp.Managed;
using Wire = Capnp.Generated.ManagedToolchain;

namespace CMakeWorkflow.Tooling.Tools;

internal static class ManagedToolchainWire
{
    internal const int MaximumFrameBytes = 128 * 1024;
    internal static readonly MessageLimits Limits = MessageLimits.Default with
    {
        MaxMessageBytes = MaximumFrameBytes - 8,
        MaxSegmentBytes = MaximumFrameBytes - 8,
        MaxBuilderBytes = MaximumFrameBytes - 8,
        MaxCanonicalBytes = MaximumFrameBytes - 8,
        MaxSegments = 1,
        MaxListElements = 1024,
        MaxTextBytes = 8192
    };

    internal static string Identity(ReadOnlySpan<byte> domain, ReadOnlySpan<byte> frame)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(domain);
        hash.AppendData(frame);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    internal static void WriteRelease(Wire.Release.Builder value, ManagedToolRelease release)
    {
        value.SetId(release.Id);
        value.SetVersion(release.Version);
        value.SetPlatform(release.Platform);
        value.SetSource(release.Source);
        value.SetArchiveSha256(release.Sha256);
        value.SetTreeSha256(release.TreeSha256);
        value.SetTarRoot(release.TarRoot);
    }

    internal static ManagedToolRelease ReadRelease(Wire.Release.Reader value) =>
        new(value.DecodeId() ?? "", value.DecodeVersion() ?? "", value.DecodePlatform() ?? "",
            value.DecodeSource() ?? "", value.DecodeArchiveSha256() ?? "", value.DecodeTreeSha256() ?? "",
            value.DecodeTarRoot());

    internal static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken ct)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        long size = stream.Length;
        if (size is < 1 or > MaximumFrameBytes)
        {
            throw new InvalidDataException("The managed tooling document is empty or exceeds its size limit.");
        }

        byte[] bytes = new byte[(int)size];
        await stream.ReadExactlyAsync(bytes, ct);
        return stream.ReadByte() == -1
            ? bytes : throw new InvalidDataException("The managed tooling document changed during verification.");
    }

    internal static async Task PublishAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string fullPath = Path.GetFullPath(path);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            await using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
