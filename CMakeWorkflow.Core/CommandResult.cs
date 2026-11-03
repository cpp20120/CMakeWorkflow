using System.Diagnostics;
using System.Security.Cryptography;

namespace CMakeWorkflow;

public sealed record CommandResult(
    int ExitCode,
    string Output,
    bool TimedOut,
    int? ProcessId = null,
    string? Stdout = null,
    string? Stderr = null);
