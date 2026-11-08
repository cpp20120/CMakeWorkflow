namespace CMakeWorkflow.Tooling.Environment;

public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);
