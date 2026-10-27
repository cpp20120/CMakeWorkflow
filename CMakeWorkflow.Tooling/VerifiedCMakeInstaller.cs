using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Tooling;

public sealed class VerifiedCMakeInstaller(HttpClient? client = null) : ICMakeInstaller
{
    private readonly HttpClient client = client ?? CreateClient();

    public async Task InstallAsync(string installDirectory, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("Verified CMake release fallback currently supports Linux.");
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64", Architecture.Arm64 => "aarch64",
            _ => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64.")
        };
        using var releaseResponse = await client.GetAsync(
            "https://api.github.com/repos/Kitware/CMake/releases/latest", ct);
        releaseResponse.EnsureSuccessStatusCode();
        await using var releaseBody = await releaseResponse.Content.ReadAsStreamAsync(ct);
        using var release = await JsonDocument.ParseAsync(releaseBody, cancellationToken: ct);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString()
            ?? throw new InvalidDataException("CMake release has no tag.");
        var version = tag.TrimStart('v');
        if (!Version.TryParse(version, out var parsed) || parsed < new Version(3, 26))
            throw new InvalidDataException($"Unsupported CMake release tag '{tag}'.");
        var name = $"cmake-{version}-linux-{arch}.tar.gz";
        JsonElement? asset = null;
        foreach (var item in root.GetProperty("assets").EnumerateArray())
            if (item.GetProperty("name").GetString() == name) { asset = item; break; }
        if (asset is null) throw new InvalidDataException($"CMake release {tag} lacks {name}.");
        var url = asset.Value.GetProperty("browser_download_url").GetString();
        var digest = asset.Value.TryGetProperty("digest", out var field) ? field.GetString() : null;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
            || !uri.AbsolutePath.StartsWith("/Kitware/CMake/releases/download/", StringComparison.Ordinal))
            throw new InvalidDataException("CMake release asset URL is not the expected publisher URL.");
        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
            || digest.Length != "sha256:".Length + 64)
            throw new InvalidDataException("CMake release does not publish a SHA-256 asset digest.");

        var store = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        var baseDirectory = store is { Length: > 0 } ? Path.GetFullPath(store)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "CMakeWorkflow", "tooling", "cmake");
        Directory.CreateDirectory(baseDirectory);
        var archive = Path.Combine(baseDirectory, $".{Guid.NewGuid():N}.tar.gz");
        var staging = Path.Combine(baseDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = new FileStream(archive, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                var buffer = new byte[131072]; long total = 0;
                while (true)
                {
                    var count = await input.ReadAsync(buffer, ct);
                    if (count == 0) break;
                    total += count;
                    if (total > 512L * 1024 * 1024)
                        throw new InvalidDataException("CMake archive exceeds the 512 MiB limit.");
                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            await using (var source = File.OpenRead(archive))
            {
                var actual = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("CMake release SHA-256 verification failed.");
            }
            Directory.CreateDirectory(staging);
            await using (var source = File.OpenRead(archive))
            await using (var gzip = new GZipStream(source, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false,
                    cancellationToken: ct);
            var extracted = Path.Combine(staging, $"cmake-{version}-linux-{arch}");
            foreach (var executable in new[] { "cmake", "ctest", "cpack" })
            {
                var path = Path.Combine(extracted, "bin", executable);
                if (!File.Exists(path) || !IsInside(extracted,
                    new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path))
                    throw new InvalidDataException($"CMake archive lacks bin/{executable}.");
            }
            var installed = Path.Combine(baseDirectory, $"{version}-{arch}");
            if (!Directory.Exists(installed)) Directory.Move(extracted, installed);
            Directory.CreateDirectory(installDirectory);
            foreach (var executable in new[] { "cmake", "ctest", "cpack" })
            {
                var destination = Path.Combine(installDirectory, executable);
                if (File.Exists(destination))
                {
                    var existing = new FileInfo(destination);
                    if (existing.LinkTarget is null || !IsInside(baseDirectory,
                        existing.ResolveLinkTarget(true)?.FullName ?? ""))
                        throw new IOException($"Refusing to replace an unrelated executable: {destination}");
                }
            }
            foreach (var executable in new[] { "cmake", "ctest", "cpack" })
            {
                var destination = Path.Combine(installDirectory, executable);
                var temporary = destination + $".{Guid.NewGuid():N}.tmp";
                File.CreateSymbolicLink(temporary, Path.Combine(installed, "bin", executable));
                File.Move(temporary, destination, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(archive)) File.Delete(archive);
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
    }

    private static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }

    private static HttpClient CreateClient()
    {
        var result = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeWorkflow-Tooling/1.0");
        return result;
    }
}
