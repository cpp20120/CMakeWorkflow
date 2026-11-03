using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed record CMakeTargetRef(string BuildDirectory, string Target, string? Configuration);
