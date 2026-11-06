// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

// Explicit, typed Linux host mutations. No shell, sudo, or arbitrary paths from profiles.
public sealed record HostSettingChange(string Id, string Path, string? Before, string After);
