namespace CMakeWorkflow.CMake.Toolchains;

// Keep the environment in memory; never put inherited variables or tokens in run.json.
public sealed record WorkflowToolchainRuntime(
    string CMake,
    string? Ninja,
    string Compiler,
    string? Sdk,
    string? EnvironmentProfileId,
    string Generator,
    string Fingerprint,
    IReadOnlyDictionary<string, string> Variables)
{
    public WorkflowVcpkgConfiguration? Vcpkg { get; init; }
    public LinuxCompilerConfiguration? LinuxCompiler { get; init; }
    public LinuxToolchainIdentity? Linux { get; init; }
    public string? CCompiler { get; init; }
    public string? CTest { get; init; }
    public bool RequireConfiguredFingerprint { get; init; }
}
