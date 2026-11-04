namespace CMakeWorkflow.Client;

/// <summary>SSH transport failed before a usable Host connection was established.</summary>
public sealed class SshConnectionException(string message, string diagnostics, int? exitCode, Exception inner)
    : IOException(message, inner)
{
    public string Diagnostics { get; } = diagnostics;
    public int? ExitCode { get; } = exitCode;
}
