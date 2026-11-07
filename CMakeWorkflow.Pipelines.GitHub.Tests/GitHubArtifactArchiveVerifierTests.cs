using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Pipelines.Artifacts;
using CMakeWorkflow.Pipelines.GitHub;

internal static class GitHubArtifactArchiveVerifierTests
{
    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "cmwf-p5b-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            await VerifyAndPublishAsync(root);
            await RefuseMismatchAsync(root);
            await RefuseTraversalAsync(root);
            await RefuseSymlinkAsync(root);
            await RefuseCaseCollisionAsync(root);
            await RefuseMissingDigestAsync(root);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
        Console.WriteLine("P5b GitHub archive: SHA-256, atomic publish, zip-slip, symlink and collision refusal passed.");
    }

    private static FederatedArtifact Artifact(byte[] archive, string? hash = null) => new(
        new(ArtifactOrigin.GitHubActions, "pipeline-1", "ci", "github:CPP20120/CMAKEWORKFLOW:99", "12"),
        "artifact", archive.LongLength, hash ?? Convert.ToHexString(SHA256.HashData(archive)),
        ArtifactDigestEvidence.ProviderReported, ArtifactExecutionBinding.RunOnly, false, null);

    private static byte[] Zip(params (string Name, string Body, bool Symlink)[] entries)
    {
        using MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach ((string name, string body, bool symlink) in entries)
            {
                ZipArchiveEntry entry = zip.CreateEntry(name);
                if (symlink)
                {
                    entry.ExternalAttributes = unchecked((int)0xA0000000);
                }
                using Stream stream = entry.Open();
                byte[] content = Encoding.UTF8.GetBytes(body);
                stream.Write(content);
            }
        }
        return buffer.ToArray();
    }

    private static async Task VerifyAndPublishAsync(string root)
    {
        byte[] archive = Zip(("out/result.txt", "hello", false), ("log.txt", "test", false));
        string destination = Path.Combine(root, "good");
        using MemoryStream source = new(archive);
        GitHubArtifactExtractionReceipt receipt = await GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive), source, destination);
        Check(receipt.Files.Count == 2 && receipt.ArchiveSha256 == Convert.ToHexString(SHA256.HashData(archive)) &&
            receipt.Key.ArtifactId == "12" &&
            await File.ReadAllTextAsync(Path.Combine(destination, "out", "result.txt")) == "hello" &&
            receipt.Files.Single(f => f.RelativePath == "out/result.txt").Sha256 ==
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("hello"))),
            "Archive was not independently verified and published.");
        using MemoryStream replay = new(archive);
        await RefusedAsync<IOException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive), replay, destination));
    }

    private static async Task RefuseMismatchAsync(string root)
    {
        byte[] archive = Zip(("safe.txt", "hello", false));
        string destination = Path.Combine(root, "mismatch");
        using MemoryStream source = new(archive);
        await RefusedAsync<InvalidDataException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive, new string('F', 64)), source, destination));
        Check(!Directory.Exists(destination), "Mismatched archive was published.");
    }

    private static async Task RefuseTraversalAsync(string root)
    {
        byte[] archive = Zip(("../escape.txt", "oops", false));
        string destination = Path.Combine(root, "traversal");
        using MemoryStream source = new(archive);
        await RefusedAsync<InvalidDataException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive), source, destination));
        Check(!Directory.Exists(destination) && !File.Exists(Path.Combine(root, "escape.txt")),
            "ZIP path traversal created files outside extraction root.");
    }

    private static async Task RefuseSymlinkAsync(string root)
    {
        byte[] archive = Zip(("link", "target", true));
        using MemoryStream source = new(archive);
        await RefusedAsync<InvalidDataException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive), source, Path.Combine(root, "symlink")));
    }

    private static async Task RefuseCaseCollisionAsync(string root)
    {
        byte[] archive = Zip(("Foo.txt", "one", false), ("foo.txt", "two", false));
        using MemoryStream source = new(archive);
        await RefusedAsync<InvalidDataException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            Artifact(archive), source, Path.Combine(root, "collision")));
    }

    private static async Task RefuseMissingDigestAsync(string root)
    {
        byte[] archive = Zip(("safe.txt", "hello", false));
        FederatedArtifact unknown = Artifact(archive) with
        {
            DigestEvidence = ArtifactDigestEvidence.NotPublished,
            Sha256 = null
        };
        using MemoryStream source = new(archive);
        await RefusedAsync<InvalidDataException>(() => GitHubArtifactArchiveVerifier.ExtractAsync(
            unknown, source, Path.Combine(root, "unknown")));
        Check(source.Position == 0, "Unverifiable artifact unexpectedly consumed stream.");
    }

    private static void Check(bool condition, string reason)
    {
        if (!condition)
        {
            throw new UnreachableException(reason);
        }
    }

    private static async Task RefusedAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }
        throw new UnreachableException("Expected refusal " + typeof(T).Name);
    }
}
