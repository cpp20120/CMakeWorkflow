using System.Diagnostics;

namespace CMakeWorkflow.Client.Transport;

/// <summary>Builds binary SSH bridge commands with separate local argv and remote shell quoting.</summary>
/// <remarks>
/// No process is started here. Local arguments use ProcessStartInfo.ArgumentList; remote executable and
/// endpoint tokens are POSIX single-quoted. Remote paths remain remote strings; validation is not existence checking.
/// </remarks>
internal static class SshCommand
{
    /// <summary>Creates a noninteractive OpenSSH launch specification for a persistent Host bridge.</summary>
    /// <remarks>
    /// Disables PTY and credential prompts, redirects protocol streams, and leaves host authentication to
    /// OpenSSH policy. Destination cannot be an option string. The SDK applies the overall connection deadline separately.
    /// </remarks>
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

    /// <summary>Quotes an exec command that attaches to an existing remote Host without owning it.</summary>
    internal static string BridgeCommand(string executable, string endpoint)
    {
        Text(executable, nameof(executable));
        RemotePath(endpoint, nameof(endpoint));
        return $"exec {Quote(executable)} host bridge --stdio --endpoint {Quote(endpoint)}";
    }

    /// <summary>Quotes an exec command whose channel owns a private Host lifetime.</summary>
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

    /// <summary>Rejects nonabsolute Linux paths, double-slash roots and dot segments without local resolution.</summary>
    /// <remarks>
    /// Does not check remote existence, canonicalize symlinks or authorize workspace containment; the Host
    /// performs those checks after initialization. This validation is shared by OpenSSH and SSH.NET transports.
    /// </remarks>
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
