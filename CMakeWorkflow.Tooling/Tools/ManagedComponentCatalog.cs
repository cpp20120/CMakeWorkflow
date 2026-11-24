namespace CMakeWorkflow.Tooling.Tools;

internal enum ComponentKind { BuildTools, Compiler, PlatformSdk, DependencyProvider, Registry }
internal enum ComponentArchive { TarGzip, TarXz, ZipExecutable, RawExecutable }

internal sealed record ToolchainHost(string Os, string Architecture);
internal sealed record ToolchainTarget(string Os, string Architecture, string Abi, string Triple);
internal sealed record ComponentRequirement(string Id, string Version);

internal sealed record ManagedComponent(
    ManagedToolRelease Release,
    ComponentKind Kind,
    ToolchainHost Host,
    ToolchainTarget[] Targets,
    ComponentArchive Format,
    string[] Tools,
    string[] Executables,
    ComponentRequirement[] Requires,
    string[] RuntimeRequirements,
    string Provider = "managed-release",
    bool ProfileDefault = true);

internal static class ManagedComponentCatalog
{
    internal static ToolchainHost LinuxHost { get; } = new("linux", "x64");
    internal static ToolchainTarget LinuxTarget { get; } = new("linux", "x64", "gnu", ManagedLinuxSdk.TargetTriple);

    // Preserve ManagedToolRelease as the wire/store identity used by approved plans and the SDK.
    internal static IReadOnlyList<ManagedComponent> Components { get; } = Array.AsReadOnly<ManagedComponent>(
    [
        new(ManagedReleases.CMakeRelease, ComponentKind.BuildTools, LinuxHost, [], ComponentArchive.TarGzip,
            ["cmake", "ctest", "cpack"], ["bin/cmake", "bin/ctest", "bin/cpack"], [], []),
        new(ManagedReleases.NinjaRelease, ComponentKind.BuildTools, LinuxHost, [], ComponentArchive.ZipExecutable,
            ["ninja"], ["bin/ninja"], [], []),
        new(ManagedReleases.LlvmRelease, ComponentKind.Compiler, LinuxHost, [LinuxTarget], ComponentArchive.TarXz,
            ["clang", "clang++", "ld.lld"], ["bin/clang", "bin/clang++", "bin/ld.lld", "bin/llvm-ar", "bin/llvm-ranlib"],
            [new("sysroot", ManagedReleases.SysrootRelease.Version)], ["glibc >= 2.31", "/usr/bin/xz for installation"]),
        new(ManagedReleases.SysrootRelease, ComponentKind.PlatformSdk, LinuxHost, [LinuxTarget], ComponentArchive.TarXz,
            ["sysroot"], [], [], ["/usr/bin/xz for installation"]),
        new(ManagedReleases.VcpkgRelease, ComponentKind.DependencyProvider, LinuxHost, [], ComponentArchive.RawExecutable,
            ["vcpkg"], ["bin/vcpkg"], [new("vcpkg-registry", ManagedReleases.VcpkgCommit)], ["glibc", "git"]),
        new(ManagedReleases.VcpkgRegistry, ComponentKind.Registry, LinuxHost, [], ComponentArchive.TarGzip,
            ["vcpkg-registry"], [], [], ["git"])
    ]);

    internal static ManagedToolRelease? DefaultForTool(string tool) =>
        Components.SingleOrDefault(component => component.ProfileDefault &&
            component.Tools.Contains(tool, StringComparer.Ordinal))?.Release;

    internal static ManagedComponent? Find(string id, string version) =>
        Components.SingleOrDefault(component => component.Release.Id == id && component.Release.Version == version);
}
