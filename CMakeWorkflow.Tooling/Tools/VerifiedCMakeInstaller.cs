using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed class VerifiedCMakeInstaller(HttpClient? client = null) : ICMakeInstaller
{
    private readonly HttpClient client = client ?? CreateDownloadClient();

    public async Task InstallAsync(string installDirectory, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException("Verified CMake release fallback currently supports Linux.");
        }

        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x86_64",
            Architecture.Arm64 => "aarch64",
            Architecture.X86 => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.Arm => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.Wasm => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.S390x => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.LoongArch64 => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.Armv6 => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.Ppc64le => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            Architecture.RiscV64 => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64."),
            _ => throw new PlatformNotSupportedException("CMake fallback supports x64 and arm64.")
        };
        using HttpResponseMessage releaseResponse = await client.GetAsync(
            new Uri("https://api.github.com/repos/Kitware/CMake/releases/latest"), ct);
        _ = releaseResponse.EnsureSuccessStatusCode();
        await using Stream releaseBody = await releaseResponse.Content.ReadAsStreamAsync(ct);
        using JsonDocument release = await JsonDocument.ParseAsync(releaseBody, cancellationToken: ct);
        JsonElement root = release.RootElement;
        string tag = root.GetProperty("tag_name").GetString()
                  ?? throw new InvalidDataException("CMake release has no tag.");
        string version = tag.TrimStart('v');
        if (!Version.TryParse(version, out Version? parsed) || parsed < new Version(3, 26))
        {
            throw new InvalidDataException($"Unsupported CMake release tag '{tag}'.");
        }

        string name = $"cmake-{version}-linux-{arch}.tar.gz";
        JsonElement? asset = null;
        foreach (JsonElement item in root.GetProperty("assets").EnumerateArray())
        {
            if (item.GetProperty("name").GetString() == name)
            {
                asset = item;
                break;
            }
        }

        if (asset is null)
        {
            throw new InvalidDataException($"CMake release {tag} lacks {name}.");
        }

        string? url = asset.Value.GetProperty("browser_download_url").GetString();
        string? digest = asset.Value.TryGetProperty("digest", out JsonElement field) ? field.GetString() : null;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
                        || uri.Scheme != Uri.UriSchemeHttps || uri.Host != "github.com"
                        || !uri.AbsolutePath.StartsWith("/Kitware/CMake/releases/download/", StringComparison.Ordinal))
        {
            throw new InvalidDataException("CMake release asset URL is not the expected publisher URL.");
        }

        if (digest is null || !digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                           || digest.Length != "sha256:".Length + 64)
        {
            throw new InvalidDataException("CMake release does not publish a SHA-256 asset digest.");
        }

        string? store = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        string baseDirectory = store is { Length: > 0 }
            ? Path.GetFullPath(store)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "CMakeWorkflow", "tooling", "cmake");
        _ = Directory.CreateDirectory(baseDirectory);
        string archive = Path.Combine(baseDirectory, $".{Guid.NewGuid():N}.tar.gz");
        string staging = Path.Combine(baseDirectory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            using (HttpResponseMessage response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                _ = response.EnsureSuccessStatusCode();
                await using Stream input = await response.Content.ReadAsStreamAsync(ct);
                await using FileStream output = new(archive, FileMode.CreateNew,
                    FileAccess.Write, FileShare.None);
                byte[] buffer = new byte[131072];
                long total = 0;
                while (true)
                {
                    int count = await input.ReadAsync(buffer, ct);
                    if (count == 0)
                    {
                        break;
                    }

                    total += count;
                    if (total > 512L * 1024 * 1024)
                    {
                        throw new InvalidDataException("CMake archive exceeds the 512 MiB limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }

            await using (FileStream source = File.OpenRead(archive))
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(source, ct));
                if (!actual.Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("CMake release SHA-256 verification failed.");
                }
            }

            _ = Directory.CreateDirectory(staging);
            await using (FileStream source = File.OpenRead(archive))
            await using (GZipStream gzip = new(source, CompressionMode.Decompress))
            {
                await TarFile.ExtractToDirectoryAsync(gzip, staging, overwriteFiles: false,
                    cancellationToken: ct);
            }

            string extracted = Path.Combine(staging, $"cmake-{version}-linux-{arch}");
            foreach (string? executable in new[] { "cmake", "ctest", "cpack" })
            {
                string path = Path.Combine(extracted, "bin", executable);
                if (!File.Exists(path) || !IsInside(extracted,
                        new FileInfo(path).ResolveLinkTarget(true)?.FullName ?? path))
                {
                    throw new InvalidDataException($"CMake archive lacks bin/{executable}.");
                }
            }

            string installed = Path.Combine(baseDirectory, $"{version}-{arch}");
            if (!Directory.Exists(installed))
            {
                Directory.Move(extracted, installed);
            }

            _ = Directory.CreateDirectory(installDirectory);
            foreach (string? executable in new[] { "cmake", "ctest", "cpack" })
            {
                string destination = Path.Combine(installDirectory, executable);
                if (File.Exists(destination))
                {
                    FileInfo existing = new(destination);
                    if (existing.LinkTarget is null || !IsInside(baseDirectory,
                            existing.ResolveLinkTarget(true)?.FullName ?? ""))
                    {
                        throw new IOException($"Refusing to replace an unrelated executable: {destination}");
                    }
                }
            }

            foreach (string? executable in new[] { "cmake", "ctest", "cpack" })
            {
                string destination = Path.Combine(installDirectory, executable);
                string temporary = destination + $".{Guid.NewGuid():N}.tmp";
                _ = File.CreateSymbolicLink(temporary, Path.Combine(installed, "bin", executable));
                File.Move(temporary, destination, overwrite: true);
            }
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }

            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string relative = Path.GetRelativePath(root, path);
        return relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar,
            StringComparison.Ordinal) && !Path.IsPathFullyQualified(relative);
    }

    public static HttpClient CreateDownloadClient()
    {
        HttpClient result = new() { Timeout = TimeSpan.FromMinutes(10) };
        result.DefaultRequestHeaders.UserAgent.ParseAdd("CMakeWorkflow-Tooling/1.0");
        return result;
    }
}