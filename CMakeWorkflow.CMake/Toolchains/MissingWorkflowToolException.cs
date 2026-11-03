using System.Diagnostics;
using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow;

// A missing executable is a recoverable pre-execution admission failure, not
// proof that the workflow operation ran and failed.
public sealed class MissingWorkflowToolException(string executable, Exception inner)
    : IOException($"Required executable '{executable}' could not be started.", inner)
{
    public string Executable { get; } = executable;
}
