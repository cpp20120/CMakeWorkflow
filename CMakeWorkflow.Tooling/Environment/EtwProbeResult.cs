using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);
