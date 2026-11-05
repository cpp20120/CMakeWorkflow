#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow;
#pragma warning restore IDE0130

public sealed record NativeTargetObservation(
    string Name,
    string Configuration,
    string Type,
    string? Executable,
    string Evidence);
