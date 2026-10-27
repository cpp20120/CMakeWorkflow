using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public sealed record ToolCommandResult(int ExitCode, string Output, string Error);

public interface IToolingRuntime
{
    ToolPlatform Platform { get; }
    bool IsAdministrator { get; }
    string? Resolve(string command, string installDirectory);
    Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct);
}

public sealed class SystemToolingRuntime : IToolingRuntime
{
    public ToolPlatform Platform => OperatingSystem.IsWindows() ? ToolPlatform.Windows
        : OperatingSystem.IsMacOS() ? ToolPlatform.MacOS : ToolPlatform.Linux;
    public bool IsAdministrator => OperatingSystem.IsWindows() ||
        !OperatingSystem.IsWindows() && GetEffectiveUserId() == 0;

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    public string? Resolve(string command, string installDirectory)
    {
        var suffixes = Platform == ToolPlatform.Windows
            ? new[] { "", ".exe", ".cmd", ".bat" } : [""];
        var directories = new[] { installDirectory }.Concat((Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));
        foreach (var directory in directories)
            foreach (var suffix in suffixes)
            {
                try
                {
                    var path = Path.Combine(directory, command + suffix);
                    if (File.Exists(path)) return Path.GetFullPath(path);
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException) { }
            }
        return null;
    }

    public async Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {executable}.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException($"Tool command timed out after {timeout}: {executable}.");
        }
        return new(process.ExitCode, await output, await error);
    }
}
