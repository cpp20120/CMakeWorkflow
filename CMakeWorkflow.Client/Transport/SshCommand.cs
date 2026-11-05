using System.Diagnostics;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

internal static class SshCommand
{
    internal static ProcessStartInfo Create(SshHostOptions options)
    {
        Text(options.Destination, nameof(options.Destination));
        if (options.Destination.StartsWith('-') || options.Destination.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("SSH destination must be a host alias or user@host, not options.");
        }

        Text(options.RemoteExecutable, nameof(options.RemoteExecutable));
        Text(options.SshExecutable, nameof(options.SshExecutable));
        RemotePath(options.Endpoint, nameof(options.Endpoint));
        RemotePath(options.Workspace, nameof(options.Workspace));
        if (options.ConnectionTimeout <= TimeSpan.Zero || options.ConnectionTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Use a timeout between zero and one hour.");
        }

        ProcessStartInfo start = new(options.SshExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        // Host keys and authentication remain OpenSSH's responsibility. BatchMode
        // prevents credential prompts from hanging an SDK caller. No TTY/framing changes.
        foreach (string? arg in new[] { "-T", "-o", "BatchMode=yes", "--", options.Destination })
        {
            start.ArgumentList.Add(arg);
        }

        start.ArgumentList.Add(BridgeCommand(options.RemoteExecutable, options.Endpoint));
        return start;
    }

    internal static string BridgeCommand(string executable, string endpoint)
    {
        Text(executable, nameof(executable));
        RemotePath(endpoint, nameof(endpoint));
        return $"exec {Quote(executable)} host bridge --stdio --endpoint {Quote(endpoint)}";
    }

    internal static string StdioHostCommand(string executable)
    {
        Text(executable, nameof(executable));
        // The exec channel itself owns the private Host; no remote socket needed.
        return $"exec {Quote(executable)} host serve --stdio";
    }

    private static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static void Text(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A nonempty value without control characters is required.", name);
        }
    }

    internal static void RemotePath(string path, string name)
    {
        Text(path, name);
        if (!path.StartsWith('/') || path.StartsWith("//", StringComparison.Ordinal)
            || path.Split('/').Any(component => component is "." or ".."))
        {
            throw new ArgumentException("Specify an absolute Linux remote path without dot segments; local paths are not translated.", name);
        }
    }
}
