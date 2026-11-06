// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);
