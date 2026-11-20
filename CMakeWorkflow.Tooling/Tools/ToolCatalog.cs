namespace CMakeWorkflow.Tooling.Tools;

public static class ToolCatalog
{
    private static readonly Dictionary<string, ToolDefinition> Tools = BuildTools();
    private static readonly Dictionary<string, ToolProfile> Profiles = BuildProfiles();

    internal static ManagedToolRelease CMakeRelease { get; } = new("cmake", "4.1.2", ManagedToolStore.Platform,
        "https://github.com/Kitware/CMake/releases/download/v4.1.2/cmake-4.1.2-linux-x86_64.tar.gz",
        "773cc679c3a7395413bd096523f8e5d6c39f8718af4e12eb4e4195f72f35e4ab",
        "e4cfa37816fc2e84fc9f6534beeb72149966085c84240d13c03ae67b438f13e1",
        "cmake-4.1.2-linux-x86_64");

    internal static ManagedToolRelease NinjaRelease { get; } = new("ninja", "1.13.1", ManagedToolStore.Platform,
        "https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-linux.zip",
        "0830252db77884957a1a4b87b05a1e2d9b5f658b8367f82999a941884cbe0238",
        "5c8e393cce8ef69a21ae3889db04b419abf52801151d78ffd39df9a05dc454b2",
        null);

    // Chromium 138.0.7204.49 compiler package and Chromium's Bullseye amd64 link sysroot.
    internal static ManagedToolRelease LlvmRelease { get; } = new("llvm", "21.0.0", ManagedToolStore.Platform,
        "https://commondatastorage.googleapis.com/chromium-browser-clang/Linux_x64/clang-llvmorg-21-init-11777-gfd3fecfc-1.tar.xz",
        "5f69279b3697166facfc354634157e0a8a32fa6e36864200ad8a8f85add3f3f6",
        "15b40a305d21a2b70a825be3fca704b57ba75583de0c821efc1de5e0618ad4e7", "");

    internal static ManagedToolRelease SysrootRelease { get; } = new("sysroot", "debian-bullseye-glibc-2.31",
        ManagedToolStore.Platform,
        "https://commondatastorage.googleapis.com/chrome-linux-sysroot/f851add76bdae472fb6c94b4f920bc4a132d85ac607951391297f636af7e934e",
        "f851add76bdae472fb6c94b4f920bc4a132d85ac607951391297f636af7e934e",
        "a4278a1c39844aa493707d21c8a87167ba606277b39cab706b425e47433fe36a", "");

    internal const string VcpkgCommit = "84bab45d415d22042bd0b9081aea57f362da3f35";

    internal static ManagedToolRelease VcpkgRegistry { get; } = new("vcpkg-registry", VcpkgCommit, ManagedToolStore.Platform,
        "https://codeload.github.com/microsoft/vcpkg/tar.gz/" + VcpkgCommit,
        "e8731530753279ac15e760c010b1724439afdf057c4fb6cb2ffc04f7680a4953",
        "174a71d0508accfc0670ae8cf9fb34c1bd4a383be8d9bce5f763d2a422008b74", "vcpkg-" + VcpkgCommit);

    internal static ManagedToolRelease VcpkgRelease { get; } = new("vcpkg", "2025-12-05", ManagedToolStore.Platform,
        "https://github.com/microsoft/vcpkg-tool/releases/download/2025-12-05/vcpkg-glibc",
        "54098727c0bcc338308dd42c7a94427816f9127e8393864684b7d9cdebbfe351",
        "65cf780c53fd2d9f19a691b40afe7f776d0d4c088fbbe91a84a62d37374cd331", null);

    internal static ManagedToolRelease? ManagedRelease(string tool) => tool switch
    {
        "cmake" or "ctest" => CMakeRelease,
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