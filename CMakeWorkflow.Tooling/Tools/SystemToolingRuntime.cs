using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Tools;

public sealed class SystemToolingRuntime(string? workingDirectory = null) : IToolingRuntime
{
    public ToolPlatform Platform => OperatingSystem.IsWindows() ? ToolPlatform.Windows
        : OperatingSystem.IsMacOS() ? ToolPlatform.MacOS : ToolPlatform.Linux;

    public bool IsAdministrator => OperatingSystem.IsWindows() ||
                                   (!OperatingSystem.IsWindows() && GetEffectiveUserId() == 0);

    [DllImport("libc", EntryPoint = "geteuid")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern uint GetEffectiveUserId();

    public string? Resolve(string command, string installDirectory)
    {
        string[] suffixes = Platform == ToolPlatform.Windows
            ? ["", ".exe", ".cmd", ".bat"]
            : [""];
        IEnumerable<string> directories = new[] { installDirectory }.Concat((System.Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (string? directory in directories)
        {
            foreach (string? suffix in suffixes)
            {
                try
                {
                    string path = Path.Combine(directory, command + suffix);
                    if (File.Exists(path))
                    {
                        return Path.GetFullPath(path);
                    }
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException)
                {
                }
            }
        }

        return null;
    }

    public async Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ProcessStartInfo start = new(executable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workingDirectory ?? ""
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(ct);
        Task<string> error = process.StandardError.ReadToEndAsync(ct);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
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

            throw new TimeoutException($"Tool command timed out after {timeout}: {executable}.");
        }

        return new(process.ExitCode, await output, await error);
    }
}