namespace CMakeWorkflow.CMake.Toolchains;

public sealed record LinuxToolchainIdentity(
    WorkflowToolSelection CMake,
    WorkflowToolSelection CTest,
    WorkflowToolSelection Ninja,
    string CCompiler,
    string CCompilerSha256,
    string CxxCompilerSha256,
    string TargetTriple);
