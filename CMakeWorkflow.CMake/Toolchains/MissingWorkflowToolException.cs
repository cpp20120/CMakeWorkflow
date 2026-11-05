using System.Diagnostics.CodeAnalysis;
#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

// A missing executable is a recoverable pre-execution admission failure, not
// proof that the workflow operation ran and failed.
[SuppressMessage("Design", "CA1032", Justification = "Existing constructors require domain-specific error evidence; generic constructors would create invalid exception state.")]
public sealed class MissingWorkflowToolException(string executable, Exception inner)
    : IOException($"Required executable '{executable}' could not be started.", inner)
{
    public string Executable { get; } = executable;
}
