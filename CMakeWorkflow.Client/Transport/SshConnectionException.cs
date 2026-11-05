using System.Diagnostics.CodeAnalysis;
namespace CMakeWorkflow.Client.Transport;

/// <summary>SSH transport failed before a usable Host connection was established.</summary>
[SuppressMessage("Design", "CA1032", Justification = "Existing constructors require domain-specific error evidence; generic constructors would create invalid exception state.")]
public sealed class SshConnectionException(string message, string diagnostics, int? exitCode, Exception inner)
    : IOException(message, inner)
{
    public string Diagnostics { get; } = diagnostics;
    public int? ExitCode { get; } = exitCode;
}
