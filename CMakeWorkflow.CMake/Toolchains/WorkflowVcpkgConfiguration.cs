namespace CMakeWorkflow.CMake.Toolchains;

public sealed record WorkflowVcpkgConfiguration(string ManifestRoot, string InstalledDirectory, string VcpkgToolchain,
    string ChainloadToolchain, string Triplet)
{
    internal bool NativeKit { get; init; }
}
