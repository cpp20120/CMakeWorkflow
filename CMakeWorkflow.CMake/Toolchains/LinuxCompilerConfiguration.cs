namespace CMakeWorkflow.CMake.Toolchains;

public sealed record LinuxCompilerConfiguration(string Sysroot, string TargetTriple, string Linker,
    string Archiver, string Ranlib, string DriverFlags);
