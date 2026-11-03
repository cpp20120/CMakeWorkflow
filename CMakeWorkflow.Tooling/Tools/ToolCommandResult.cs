using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public sealed record ToolCommandResult(int ExitCode, string Output, string Error);
