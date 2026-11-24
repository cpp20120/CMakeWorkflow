namespace CMakeWorkflow.Tooling.Tools;

public static class ToolCatalog
{
    private static readonly Dictionary<string, ToolDefinition> Tools = BuildTools();
    private static readonly Dictionary<string, ToolProfile> Profiles = BuildProfiles();

    internal static ManagedToolRelease CMakeRelease => ManagedReleases.CMakeRelease;
    internal static ManagedToolRelease NinjaRelease => ManagedReleases.NinjaRelease;
    internal static ManagedToolRelease LlvmRelease => ManagedReleases.LlvmRelease;
    internal static ManagedToolRelease SysrootRelease => ManagedReleases.SysrootRelease;
    internal static ManagedToolRelease VcpkgRegistry => ManagedReleases.VcpkgRegistry;
    internal static ManagedToolRelease VcpkgRelease => ManagedReleases.VcpkgRelease;
    internal const string VcpkgCommit = ManagedReleases.VcpkgCommit;

    internal static ManagedToolRelease? ManagedRelease(string tool) => tool switch
    {
        "cmake" or "ctest" or "cpack" => CMakeRelease,
        "ninja" => NinjaRelease,
        "clang" or "clang++" or "ld.lld" => LlvmRelease,
        "sysroot" => SysrootRelease,
        "vcpkg" => VcpkgRelease,
        "vcpkg-registry" => VcpkgRegistry,
        _ => null
    };

    public static IReadOnlyList<ToolProfile> AllProfiles => Profiles.Values.OrderBy(x => x.Id,
        StringComparer.Ordinal).ToArray();

    public static ToolDefinition GetTool(string id) => Tools.TryGetValue(id, out ToolDefinition? value)
        ? value
        : throw new ArgumentException($"Unknown tool '{id}'.");

    // Built-in profile IDs have canonical spellings. Resolve by ordinal,
    // case-insensitive lookup, without rewriting arbitrary user input.
    public static string CanonicalProfileId(string id) =>
        Profiles.TryGetValue(id, out ToolProfile? value) ? value.Id : id;

    public static IReadOnlyList<ToolDefinition> ResolveProfiles(IEnumerable<string> profileIds,
        ToolPlatform platform)
    {

        string[] ids = [.. profileIds.Select(x => x.Trim())];

        if (ids.Length == 0)
        {
            ids = ["cmake"];
        }

        HashSet<string> tools = new(StringComparer.Ordinal);
        foreach (string? id in ids)
        {
            if (!Profiles.TryGetValue(id, out ToolProfile? profile))
            {
                throw new ArgumentException(
                    $"Unknown tooling profile '{id}'. Available: {string.Join(", ", Profiles.Keys.Order(StringComparer.Ordinal))}.");
            }

            foreach (string tool in profile.Tools)
            {
                _ = tools.Add(tool == "native-profiler"
                    ? platform switch
                    {
                        ToolPlatform.Linux => "perf",
                        ToolPlatform.Windows => "wpr",
                        ToolPlatform.MacOS => "xctrace",
                        _ => "xctrace"
                    }
                    : tool);
            }
        }

        return tools.Order(StringComparer.Ordinal).Select(GetTool).ToArray();
    }

    private static Dictionary<string, ToolProfile> BuildProfiles() => new[]
    {
        new ToolProfile("native", "CMake, CTest, C++ compiler and Ninja for native workflows",
            ["cmake", "ctest", "cxx", "ninja"]),
        new ToolProfile("cmake", "Configure, build, run and CTest native targets",
            ["cmake", "ctest", "cxx"]),
        new ToolProfile("cmake-ninja", "CMake, CTest and Ninja without a compiler or SDK",
            ["cmake", "ctest", "ninja"]),
        new ToolProfile("linux-clang", "Pinned Linux Clang, LLD, sysroot, CMake, CTest and Ninja",
            ["cmake", "ctest", "clang", "clang++", "ld.lld", "sysroot", "ninja"]),
        new ToolProfile("vcpkg", "Pinned vcpkg tools and registry checkout",
            ["vcpkg", "vcpkg-registry"]),
        new ToolProfile("ninja", "Ninja generator support", ["ninja"]),
        new ToolProfile("packaging", "CPack native product packaging", ["cpack"]),
        new ToolProfile("profiling", "Native capture backend for this OS",
            ["cmake", "ctest", "cxx", "native-profiler"]),
        new ToolProfile("fuzz", "Clang-based fuzz build and corpus campaigns",
            ["cmake", "ctest", "clangxx"]),
        new ToolProfile("development", "Build CMakeWorkflow itself with .NET 10",
            ["dotnet", "cmake", "ctest", "cxx"]),
        new ToolProfile("all", "All relevant tools for this OS",
            ["cmake", "ctest", "cxx", "ninja", "clangxx", "native-profiler", "dotnet"])
    }.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, ToolDefinition> BuildTools()
    {
        static Dictionary<PackageManagerKind, string[]> Packages(string? apt = null,
            string? pacman = null, string? dnf = null, string? brew = null, string? winget = null)
        {
            Dictionary<PackageManagerKind, string[]> result = [];
            if (apt is not null)
            {
                result.Add(PackageManagerKind.Apt, [apt]);
            }

            if (pacman is not null)
            {
                result.Add(PackageManagerKind.Pacman, [pacman]);
            }

            if (dnf is not null)
            {
                result.Add(PackageManagerKind.Dnf, [dnf]);
            }

            if (brew is not null)
            {
                result.Add(PackageManagerKind.Brew, [brew]);
            }

            if (winget is not null)
            {
                result.Add(PackageManagerKind.Winget, [winget]);
            }

            return result;
        }

        return new[]
        {
            new ToolDefinition("cmake", "CMake >= 3.26", ["cmake"], ["--version"], new(3, 26),
                Packages("cmake", "cmake", "cmake", "cmake", "Kitware.CMake"), true),
            new ToolDefinition("ctest", "CTest >= 3.26", ["ctest"], ["--version"], new(3, 26),
                Packages("cmake", "cmake", "cmake", "cmake", "Kitware.CMake"), true),
            new ToolDefinition("cpack", "CPack >= 3.26", ["cpack"], ["--version"], new(3, 26),
                Packages("cmake", "cmake", "cmake", "cmake", "Kitware.CMake")),
            new ToolDefinition("ninja", "Ninja", ["ninja", "ninja-build"], ["--version"], null,
                Packages("ninja-build", "ninja", "ninja-build", "ninja", "Ninja-build.Ninja")),
            new ToolDefinition("clang", "Managed Clang C compiler", [], [], null, Packages()),
            new ToolDefinition("clang++", "Managed Clang C++ compiler", [], [], null, Packages()),
            new ToolDefinition("ld.lld", "Managed LLD linker", [], [], null, Packages()),
            new ToolDefinition("sysroot", "Managed Debian Bullseye link sysroot", [], [], null, Packages()),
            new ToolDefinition("vcpkg", "Managed vcpkg executable", [], [], null, Packages()),
            new ToolDefinition("vcpkg-registry", "Managed vcpkg registry checkout", [], [], null, Packages()),
            new ToolDefinition("cxx", "C++ compiler", ["c++", "g++", "clang++", "cl"],
                ["--version"], null, Packages("g++", "gcc", "gcc-c++", null, null),
                ManualHint:
                "Select a C++ toolchain for the chosen generator (Xcode CLT on macOS, MSVC/Windows SDK or standalone compiler on Windows). Use toolchains/discover to locate Visual Studio toolsets."),
            new ToolDefinition("clangxx", "Clang C++ compiler", ["clang++"], ["--version"], null,
                Packages("clang", "clang", "clang", "llvm", "LLVM.LLVM")),
            new ToolDefinition("perf", "Linux perf", ["perf"], ["--version"], null,
                Packages("linux-tools-common", "perf", "perf"),
                ManualHint: "Perf event permissions and kernel-matched tools may still be required."),
            new ToolDefinition("wpr", "Windows Performance Recorder", ["wpr"], ["-help"], null,
                Packages(), ManualHint: "Install Windows Performance Toolkit from the Windows ADK."),
            new ToolDefinition("xctrace", "Instruments xctrace", ["xcrun"],
                ["xctrace", "version"], null, Packages(),
                ManualHint: "Install Xcode and select its developer directory with xcode-select."),
            new ToolDefinition("dotnet", ".NET 10 SDK", ["dotnet"], ["--version"], new(10, 0),
                Packages("dotnet-sdk-10.0", "dotnet-sdk", "dotnet-sdk-10.0", "dotnet-sdk", "Microsoft.DotNet.SDK.10"))
        }.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }
}