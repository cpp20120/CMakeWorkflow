using System.Diagnostics;
using System.Text;
using CMakeWorkflow.Core.Processes;
using CMakeWorkflow.Scripting;

namespace CMakeWorkflow.Developer;

/// <summary>Runs a trusted controller-side file-based authoring application with the installed .NET SDK.</summary>
public static class WorkflowScriptCompiler
{
    /// <summary>Captures an exact manifest snapshot using the script handoff. Callers retain their output streams.</summary>
    public static async Task<WorkflowManifest> ComposeAsync(string scriptPath, IReadOnlyList<string> arguments,
        Stream standardOutput, Stream standardError, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scriptPath);
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentNullException.ThrowIfNull(standardOutput);
        ArgumentNullException.ThrowIfNull(standardError);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        string path = Path.GetFullPath(scriptPath);
        if (!Path.GetExtension(path).Equals(".cs", StringComparison.OrdinalIgnoreCase) || !File.Exists(path))
        {
            throw new ArgumentException("Expected an existing local C# file.", nameof(scriptPath));
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-script-");
        try
        {
            string directory = Path.GetDirectoryName(path)!;
            using MemoryStream selectedSdk = new();
            await RunAsync(directory, ["--version"], selectedSdk, standardError, null, deadline.Token);
            string version = Encoding.UTF8.GetString(selectedSdk.ToArray()).Trim();
            if (!Version.TryParse(version.Split('-', 2)[0], out Version? sdk) || sdk.Major < 10)
            {
                throw new InvalidOperationException($"The script requires .NET SDK 10 or later. Selected SDK: {version}");
            }

            string response = Path.Combine(scratch.FullName, "response.json");
            // Hold the original response inode/handle across authoring. Replacing the pathname
            // cannot redirect the controller's read to a link, pipe, or another file.
            using (File.Create(response))
            {
            }
            await using FileStream handoff = new(response, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                4096, FileOptions.Asynchronous);
            await RunAsync(directory, ["run", "--file", path, "--no-launch-profile", "--", .. arguments],
                standardOutput, standardError, response, deadline.Token);
            return await WorkflowScript.ReadAsync(handoff, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new TimeoutException("The .NET file-based workflow authoring process timed out.");
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static async Task RunAsync(string directory, string[] arguments, Stream output, Stream error,
        string? response, CancellationToken ct)
    {
        ProcessStartInfo start = new("dotnet")
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        if (response is null)
        {
            _ = start.Environment.Remove(WorkflowScript.ResponseVariable);
        }
        else
        {
            start.Environment[WorkflowScript.ResponseVariable] = response;
        }
        ct.ThrowIfCancellationRequested();
        WorkflowProcessBackend.Outcome result = await WorkflowProcessBackend.RunAsync(
            new(start, output, error), null, static _ => { }, ct);
        if (result.StartError is not null)
        {
            throw new InvalidOperationException("The .NET SDK could not be started. Install .NET SDK 10 or later and make dotnet available on PATH.",
                result.StartError);
        }
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(response is null
                ? $"The .NET SDK selected for this script could not be queried (exit {result.ExitCode})."
                : $"The .NET file-based workflow authoring process exited with code {result.ExitCode}.");
        }
    }
}
