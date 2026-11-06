using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Transport;

/// <summary>SSH transport failed before a usable Host connection was established.</summary>
[SuppressMessage("Design", "CA1032", Justification = "SshConnectionException carries captured Diagnostics, ExitCode and the inner transport failure; generic constructors cannot supply that SSH failure evidence.")]
public sealed class SshConnectionException(string message, string diagnostics, int? exitCode, Exception inner)
    : IOException(message, inner)
{
    public string Diagnostics { get; } = diagnostics;
    public int? ExitCode { get; } = exitCode;
}
