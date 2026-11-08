namespace CMakeWorkflow.Tooling.Environment;

// Explicit, typed Linux host mutations. No shell, sudo, or arbitrary paths from profiles.
public sealed record HostSettingChange(string Id, string Path, string? Before, string After);
