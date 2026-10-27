namespace CMakeWorkflow.Tooling;

public static class ToolCatalog
{
    private static readonly Dictionary<string, ToolDefinition> Tools = BuildTools();
    private static readonly Dictionary<string, ToolProfile> Profiles = BuildProfiles();

    public static IReadOnlyList<ToolProfile> AllProfiles => Profiles.Values.OrderBy(x => x.Id,
        StringComparer.Ordinal).ToArray();
    public static ToolDefinition GetTool(string id) => Tools.TryGetValue(id, out var value)
        ? value : throw new ArgumentException($"Unknown tool '{id}'.");
    public static IReadOnlyList<ToolDefinition> ResolveProfiles(IEnumerable<string> profileIds,
        ToolPlatform platform)
    {
        var ids = profileIds.Select(x => x.Trim().ToLowerInvariant()).ToArray();
        if (ids.Length == 0) ids = ["cmake"];
        var tools = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!Profiles.TryGetValue(id, out var profile))
                throw new ArgumentException($"Unknown tooling profile '{id}'. Available: {string.Join(", ", Profiles.Keys.Order(StringComparer.Ordinal))}.");
            foreach (var tool in profile.Tools) tools.Add(tool == "native-profiler"
                ? platform switch { ToolPlatform.Linux => "perf", ToolPlatform.Windows => "wpr",
                    _ => "xctrace" } : tool);
        }
        return tools.Order(StringComparer.Ordinal).Select(GetTool).ToArray();
    }

    private static Dictionary<string, ToolProfile> BuildProfiles() => new[]
    {
        new ToolProfile("native", "CMake, CTest, C++ compiler and Ninja for native workflows",
            ["cmake", "ctest", "cxx", "ninja"]),
        new ToolProfile("cmake", "Configure, build, run and CTest native targets",
            ["cmake", "ctest", "cxx"]),
        new ToolProfile("ninja", "Ninja generator support", ["ninja"]),
        new ToolProfile("profiling", "Native capture backend for this OS",
            ["cmake", "ctest", "cxx", "native-profiler"]),
        new ToolProfile("fuzz", "Clang-based fuzz build and corpus campaigns",
            ["cmake", "ctest", "clangxx"]),
        new ToolProfile("development", "Build CMakeWorkflow itself with .NET 10",
            ["dotnet", "cmake", "ctest", "cxx"]),
        new ToolProfile("all", "All relevant tools for this OS",
            ["cmake", "ctest", "cxx", "ninja", "clangxx", "native-profiler", "dotnet"])
    }.ToDictionary(x => x.Id, StringComparer.Ordinal);

    private static Dictionary<string, ToolDefinition> BuildTools()
    {
        static Dictionary<PackageManagerKind, string[]> Packages(string? apt = null,
            string? pacman = null, string? dnf = null, string? brew = null, string? winget = null)
        {
            var result = new Dictionary<PackageManagerKind, string[]>();
            if (apt is not null) result.Add(PackageManagerKind.Apt, [apt]);
            if (pacman is not null) result.Add(PackageManagerKind.Pacman, [pacman]);
            if (dnf is not null) result.Add(PackageManagerKind.Dnf, [dnf]);
            if (brew is not null) result.Add(PackageManagerKind.Brew, [brew]);
            if (winget is not null) result.Add(PackageManagerKind.Winget, [winget]);
            return result;
        }
        return new[]
        {
            new ToolDefinition("cmake", "CMake >= 3.26", ["cmake"], ["--version"], new(3,26),
                Packages("cmake", "cmake", "cmake", "cmake", "Kitware.CMake"), true),
            new ToolDefinition("ctest", "CTest >= 3.26", ["ctest"], ["--version"], new(3,26),
                Packages("cmake", "cmake", "cmake", "cmake", "Kitware.CMake"), true),
            new ToolDefinition("ninja", "Ninja", ["ninja", "ninja-build"], ["--version"], null,
                Packages("ninja-build", "ninja", "ninja-build", "ninja", "Ninja-build.Ninja")),
            new ToolDefinition("cxx", "C++ compiler", ["c++", "g++", "clang++", "cl"],
                ["--version"], null, Packages("g++", "gcc", "gcc-c++", null, null),
                ManualHint: "Install Xcode Command Line Tools or MSVC Build Tools for the selected generator."),
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
            new ToolDefinition("dotnet", ".NET 10 SDK", ["dotnet"], ["--version"], new(10,0),
                Packages("dotnet-sdk-10.0", "dotnet-sdk", "dotnet-sdk-10.0", "dotnet-sdk", "Microsoft.DotNet.SDK.10"))
        }.ToDictionary(x => x.Id, StringComparer.Ordinal);
    }
}
