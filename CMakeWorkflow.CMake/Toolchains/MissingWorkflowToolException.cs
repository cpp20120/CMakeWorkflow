using System.Diagnostics.CodeAnalysis;

namespace CMakeWorkflow.CMake.Toolchains;

// A missing executable is a recoverable pre-execution admission failure, not
// proof that the workflow operation ran and failed.
[SuppressMessage("Design", "CA1032", Justification = "MissingWorkflowToolException identifies the Executable and its startup failure for tool admission diagnostics; generic constructors cannot identify the missing executable.")]
public sealed class MissingWorkflowToolException(string executable, Exception inner)
    : IOException($"Required executable '{executable}' could not be started.", inner)
{
    public string Executable { get; } = executable;
}
