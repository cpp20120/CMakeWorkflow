using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow.Tooling.Toolchains;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling.VisualStudio;

/// <summary>Windows adapter around vswhere (Setup Configuration API) and the official VS Installer.</summary>
public sealed class SystemVisualStudioSetupBackend : IVisualStudioSetupBackend
{
    private readonly SystemToolingRuntime runtime = new();

    private static string? FindVsWhere()
    {
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFilesX86);
        string candidate = Path.Combine(root, "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (File.Exists(candidate))
        {
            return candidate;
        }

        foreach (string part in (System.Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string path = Path.Combine(part.Trim('"'), "vswhere.exe");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<VisualStudioInstance>> DiscoverAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Visual Studio provisioning requires Windows.");
        }

        ToolchainInventory inventory = await WindowsToolchains.DiscoverAsync(ct);
        return inventory.VisualStudio;
    }

    public async Task<bool> HasComponentAsync(string instanceId, string componentId, CancellationToken ct)
    {
        string exe = FindVsWhere() ?? throw new FileNotFoundException("vswhere.exe is required to inspect Visual Studio components.");
        ToolCommandResult result = await runtime.RunAsync(exe,
            ["-all", "-products", "*", "-prerelease", "-requires", componentId,
                "-format", "json", "-utf8", "-nologo"], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"vswhere component query failed: {result.Error.Trim()}");
        }

        using JsonDocument parsed = JsonDocument.Parse(result.Output);
        if (parsed.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("vswhere component query did not return an array.");
        }

        foreach (JsonElement entry in parsed.RootElement.EnumerateArray())
        {
            if (entry.TryGetProperty("instanceId", out JsonElement id) && id.ValueKind == JsonValueKind.String
                && instanceId.Equals(id.GetString(), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public string? FindSetupExecutable()
    {
        string? where = FindVsWhere();
        if (where is null)
        {
            return null;
        }

        string exe = Path.Combine(Path.GetDirectoryName(where)!, "setup.exe");
        return File.Exists(exe) ? exe : null;
    }

    public async Task<int> RunInstallerAsync(string path, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Visual Studio provisioning requires Windows.");
        }

        if (!Path.IsPathFullyQualified(path) || !File.Exists(path))
        {
            throw new FileNotFoundException("Approved Visual Studio Installer executable was not found.", path);
        }

        ProcessStartInfo start = new(path)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            // VS setup.exe must not be launched from its own installation directory.
            WorkingDirectory = Path.GetTempPath()
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start)
            ?? throw new Win32Exception("Visual Studio Installer did not start.");
        Task<string> stdout = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = process.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            await process.WaitForExitAsync(deadline.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }

            if (ct.IsCancellationRequested)
            {
                throw;
            }

            throw new TimeoutException("Visual Studio Installer exceeded the two-hour limit; installation may be partial.");
        }

        // Drain both pipes even when the installer returns failure.
        _ = await stdout;
        _ = await stderr;
        return process.ExitCode;
    }
}
