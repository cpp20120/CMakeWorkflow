#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling;
#pragma warning restore IDE0130

public sealed record EtwProbeResult(string Capability, string Status, int? Win32Error, string Diagnostic);
