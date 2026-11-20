namespace CMakeWorkflow.Tooling.Tools;

public sealed record ManagedToolRelease(string Id, string Version, string Platform, string Source,
    string Sha256, string TreeSha256, string? TarRoot);
