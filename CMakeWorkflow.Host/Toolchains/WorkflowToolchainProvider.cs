using System.Collections.Frozen;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Serialization;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Core;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Host.Toolchains;

public sealed class WorkflowToolchainProvider : IWorkflowToolchainProvider
{
    private readonly string? snapshotId;
    private readonly string? snapshotRoot;

    public WorkflowToolchainProvider() { }

    internal WorkflowToolchainProvider(string? snapshotId, string? snapshotRoot)
    {
        this.snapshotId = snapshotId;
        this.snapshotRoot = snapshotRoot;
    }

    public Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return binding.EnvironmentProfileId switch
        {
            "linux-managed-clang" => ResolveLinuxAsync(binding, ToolResolutionPolicy.ManagedOnly, ct),
            _ when binding.EnvironmentLockSha256 is not null || binding.VcpkgProjectSha256 is not null =>
                throw new InvalidOperationException("Linux environment and vcpkg digests require linux-managed-clang."),
            "linux-ambient-gcc" or "linux-ambient-clang" => ResolveLinuxAsync(binding, ToolResolutionPolicy.AllowSystem, ct),
            "linux-managed-tools-system-compiler" => ResolveLinuxAsync(binding, ToolResolutionPolicy.ManagedOnly, ct),
            "linux-allow-system-tools" => ResolveLinuxAsync(binding, ToolResolutionPolicy.AllowSystem, ct),
            _ => new WindowsWorkflowToolchainProvider().ResolveAsync(binding, ct)
        };
    }

    private async Task<WorkflowToolchainRuntime> ResolveLinuxAsync(WorkflowToolchainBinding binding,
        ToolResolutionPolicy policy, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The managed tools execution profile requires Linux x86-64.");
        }

        bool explicitAmbient = binding.EnvironmentProfileId is "linux-ambient-gcc" or "linux-ambient-clang";
        bool managedCompiler = binding.EnvironmentProfileId == "linux-managed-clang";
        if (binding.HostArchitecture != "x64" || binding.TargetArchitecture != "x64" ||
            binding.Generator is not ("Ninja" or "Ninja Multi-Config") ||
            (!managedCompiler && (binding.CompilerCandidateId is not (null or "system") || binding.SdkCandidateId is not null)))
        {
            throw new InvalidOperationException(
                "This profile requires native x64 and Ninja. Use linux-managed-clang to select a managed compiler or SDK identity.");
        }

        SystemToolingRuntime runtime = new();
        ToolchainEnvironmentLock? locked = binding.EnvironmentLockSha256 is { } environmentLock
            ? await ManagedEnvironmentLocks.ReadAsync(environmentLock, ct) : null;
        if (locked is not null && locked.Generator != binding.Generator)
        {
            throw new InvalidOperationException("The workflow generator differs from its approved environment lock.");
        }
        if (locked is not null && binding.VcpkgProjectSha256 is not null &&
            locked.Components.Any(component => component.Distribution != ToolCatalog.ManagedRelease(
                ManagedComponentCatalog.Find(component.Id, component.Version)!.Tools[0])))
        {
            throw new InvalidOperationException("The vcpkg project requires the existing pinned Linux toolchain releases.");
        }
        ToolingService tooling = new(runtime, new VerifiedCMakeInstaller())
        {
            FindManagedRelease = tool => locked is null ? ToolCatalog.ManagedRelease(tool) :
                ToolchainEnvironment.ReleaseForTool(locked, tool)
        };
        string profile = managedCompiler ? "linux-clang" : "cmake-ninja";
        ToolingPlan plan = await tooling.PlanAsync([profile], PackageManagerKind.None, false, null, ct,
            resolutionPolicy: explicitAmbient ? null : policy);
        if (!plan.Ready)
        {
            string pending = string.Join(", ", plan.Tools.Where(tool => !tool.Installed).Select(tool => tool.DisplayName));
            throw new InvalidOperationException(
                $"Missing tools or SDK: {pending}. Review tooling plan --profile {profile} --tool-policy {policy} and apply it explicitly.");
        }

        ToolStatus cmake = plan.Tools.Single(t => t.Id == "cmake");
        ToolStatus ctest = plan.Tools.Single(t => t.Id == "ctest");
        ToolStatus ninja = plan.Tools.Single(t => t.Id == "ninja");
        if (!MatchesCandidate(binding.CMakeCandidateId, cmake) || !MatchesCandidate(binding.NinjaCandidateId, ninja))
        {
            throw new InvalidOperationException("The resolved tool differs from the requested managed release digest.");
        }

        ToolStatus? clang = managedCompiler ? plan.Tools.Single(t => t.Id == "clang++") : null;
        ToolStatus? sdk = managedCompiler ? plan.Tools.Single(t => t.Id == "sysroot") : null;
        ToolStatus? linker = managedCompiler ? plan.Tools.Single(t => t.Id == "ld.lld") : null;
        if ((clang is not null && !MatchesCandidate(binding.CompilerCandidateId, clang)) ||
            (sdk is not null && !MatchesCandidate(binding.SdkCandidateId, sdk)))
        {
            throw new InvalidOperationException("The selected compiler or SDK differs from the requested managed digest.");
        }

        bool gcc = binding.EnvironmentProfileId == "linux-ambient-gcc";
        string cName = explicitAmbient ? gcc ? "gcc" : "clang" : "cc";
        string cxxName = explicitAmbient ? gcc ? "g++" : "clang++" : "c++";
        string cc = managedCompiler ? plan.Tools.Single(t => t.Id == "clang").Command! :
            runtime.Resolve(cName, "") ?? throw new InvalidOperationException("The execution profile requires an installed " + cName + ".");
        string cxx = clang?.Command ?? runtime.Resolve(cxxName, "") ??
            throw new InvalidOperationException("The execution profile requires an installed " + cxxName + ".");
        Dictionary<string, string> environment = CreateEnvironment(cmake.Command!, ninja.Command!, cc, cxx, binding.Generator);
        LinuxCompilerConfiguration? compiler = null;
        string triple = managedCompiler ? ManagedLinuxSdk.TargetTriple : await ReadTargetAsync(cxx, environment, ct);
        if (sdk?.Command is { } sysroot && linker?.Command is { } ld)
        {
            string bin = Path.GetDirectoryName(ld) ?? throw new InvalidDataException("Missing managed LLVM bin directory.");
            compiler = new(sysroot, triple, ld, Path.Combine(bin, "llvm-ar"), Path.Combine(bin, "llvm-ranlib"),
                ManagedLinuxSdk.DriverFlags(sysroot, ld));
            ConfigureManagedEnvironment(environment, bin);
        }
        else if (await ReadTargetAsync(cc, environment, ct) != triple)
        {
            throw new InvalidOperationException("The host C and C++ compilers report different target triples.");
        }

        string? ambientVersion = explicitAmbient ? await VerifyAmbientCompilerAsync(
            cc, cxx, gcc, triple, environment, ct) : null;
        ManagedVcpkgInstallation? packages = binding.VcpkgProjectSha256 is { } dependencyPlan
            ? await VcpkgProjects.ResolveAsync(dependencyPlan, snapshotId, snapshotRoot, ct) : null;
        LinuxToolchainIdentity linux = new(await IdentifyAsync(cmake, ct), await IdentifyAsync(ctest, ct),
            await IdentifyAsync(ninja, ct), cc, await CommandExecution.Sha256Async(cc, ct),
            await CommandExecution.Sha256Async(cxx, ct), triple)
        {
            CompilerVersion = clang?.Version ?? ambientVersion,
            CompilerArtifactSha256 = clang?.Sha256,
            SdkArtifactSha256 = sdk?.Sha256,
            Linker = linker is null ? null : await IdentifyAsync(linker, ct),
            Runtime = managedCompiler ? ManagedLinuxSdk.Runtime : null,
            EnvironmentLockSha256 = binding.EnvironmentLockSha256,
            VcpkgProjectSha256 = binding.VcpkgProjectSha256,
            VcpkgPackageTreeSha256 = packages?.PackageTreeSha256,
            VcpkgDownloadTreeSha256 = packages?.DownloadTreeSha256
        };
        List<string> paths = [cmake.Command!, ctest.Command!, ninja.Command!];
        if (compiler is not null)
        {
            paths.AddRange([compiler.Sysroot, compiler.Linker, compiler.Archiver, compiler.Ranlib, compiler.DriverFlags]);
        }

        WorkflowVcpkgConfiguration? vcpkg = null;
        if (packages is not null)
        {
            vcpkg = new(packages.ManifestRoot, packages.InstalledDirectory, packages.VcpkgToolchain,
                packages.ChainloadToolchain, packages.Triplet);
            paths.AddRange([packages.ManifestRoot, packages.InstalledDirectory, packages.VcpkgToolchain, packages.ChainloadToolchain]);
        }

        string fingerprint = Fingerprint(linux, binding, paths, environment);
        return new(cmake.Command!, ninja.Command, cxx, sdk?.Command, binding.EnvironmentProfileId, binding.Generator, fingerprint,
            environment.ToFrozenDictionary(StringComparer.Ordinal))
        {
            Vcpkg = vcpkg,
            LinuxCompiler = compiler,
            Linux = linux,
            CCompiler = cc,
            CTest = ctest.Command,
            RequireConfiguredFingerprint = true
        };
    }

    private static string Fingerprint(LinuxToolchainIdentity linux, WorkflowToolchainBinding binding,
        IEnumerable<string> executables, IReadOnlyDictionary<string, string> environment)
    {
        using MemoryStream identity = new();
        using (BinaryWriter writer = new(identity, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(binding.EnvironmentProfileId!);
            writer.Write(binding.Generator);
            writer.Write(JsonSerializer.Serialize(linux, WorkflowJson.Default.LinuxToolchainIdentity));
            foreach (string path in executables)
            {
                writer.Write(path);
            }

            foreach ((string key, string value) in environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                writer.Write(key);
                writer.Write(value);
            }
        }

        return Convert.ToHexStringLower(SHA256.HashData(identity.GetBuffer().AsSpan(0, (int)identity.Length)));
    }

    private static bool MatchesCandidate(string? candidate, ToolStatus tool) =>
        candidate is null || (tool.Sha256 is not null && candidate == "sha256:" + tool.Sha256);

    private static async Task<WorkflowToolSelection> IdentifyAsync(ToolStatus tool, CancellationToken ct) =>
        new(tool.Origin!, tool.Version, tool.Sha256, await CommandExecution.Sha256Async(tool.Command!, ct));

    private static Dictionary<string, string> CreateEnvironment(string cmake, string ninja, string cc, string cxx,
        string generator)
    {
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        IEnumerable<string> systemPaths = path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(Path.IsPathFullyQualified);
        string childPath = string.Join(Path.PathSeparator, new[]
        {
            Path.GetDirectoryName(cmake), Path.GetDirectoryName(ninja)
        }.Concat(systemPaths).Distinct(StringComparer.Ordinal));
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = childPath,
            ["CC"] = cc,
            ["CXX"] = cxx,
            ["CMAKE_TOOLCHAIN_FILE"] = "",
            ["CMAKE_GENERATOR"] = generator,
            ["CMAKE_GENERATOR_PLATFORM"] = "",
            ["CMAKE_GENERATOR_TOOLSET"] = "",
            ["CMAKE_GENERATOR_INSTANCE"] = ""
        };
        foreach (string key in new[] { "CFLAGS", "CXXFLAGS", "LDFLAGS", "CMAKE_PREFIX_PATH", "CPATH",
                     "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "LIBRARY_PATH", "COMPILER_PATH", "GCC_EXEC_PREFIX",
                     "LD_PRELOAD", "LD_LIBRARY_PATH", "LD_AUDIT" })
        {
            if (Environment.GetEnvironmentVariable(key) is { } value)
            {
                environment.Add(key, value);
            }
        }

        return environment;
    }

    private static void ConfigureManagedEnvironment(Dictionary<string, string> environment, string bin)
    {
        environment["PATH"] = bin + Path.PathSeparator + environment["PATH"];
        foreach (string key in new[] { "CFLAGS", "CXXFLAGS", "LDFLAGS", "CMAKE_PREFIX_PATH", "GCC_EXEC_PREFIX" })
        {
            environment[key] = "";
        }

        foreach (string key in new[] { "CPATH", "C_INCLUDE_PATH", "CPLUS_INCLUDE_PATH", "LIBRARY_PATH", "COMPILER_PATH" })
        {
            environment[key] = "/dev/null";
        }
    }

    private static async Task<string> VerifyAmbientCompilerAsync(string cc, string cxx, bool gcc, string triple,
        Dictionary<string, string> environment, CancellationToken ct)
    {
        if (!triple.EndsWith("-gnu", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Ambient Linux Kits require a GNU ABI compiler.");
        }

        string? version = null;
        Dictionary<string, string?> variables = environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal);
        foreach ((string compiler, string language) in new[] { (cc, "c"), (cxx, "c++") })
        {
            CommandResult macros = await CommandExecution.RunAsync(compiler, ["-dM", "-E", "-x", language, "/dev/null"],
                "", TimeSpan.FromSeconds(15), variables, ct);
            string[] lines = (macros.Stdout ?? "").Split('\n');
            bool clang = lines.Any(line => line.StartsWith("#define __clang__ ", StringComparison.Ordinal));
            bool gnu = lines.Any(line => line.StartsWith("#define __GNUC__ ", StringComparison.Ordinal));
            string header = gcc ? "#define __VERSION__ " : "#define __clang_version__ ";
            string? observed = lines.FirstOrDefault(line => line.StartsWith(header, StringComparison.Ordinal))?[header.Length..].Trim();
            if (macros.ExitCode != 0 || (gcc ? clang || !gnu : !clang) || string.IsNullOrWhiteSpace(observed) ||
                (version is not null && version != observed))
            {
                throw new InvalidOperationException("The selected C and C++ compilers do not match the Kit compiler family and version.");
            }
            version = observed;
        }
        return version!;
    }

    private static async Task<string> ReadTargetAsync(string compiler, Dictionary<string, string> environment,
        CancellationToken ct)
    {
        CommandResult result = await CommandExecution.RunAsync(compiler, ["-dumpmachine"], "",
            TimeSpan.FromSeconds(15), environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.Ordinal), ct);
        string triple = result.Stdout?.Trim() ?? "";
        return result.ExitCode == 0 && triple.StartsWith("x86_64-", StringComparison.Ordinal) &&
               triple.Contains("-linux", StringComparison.Ordinal) && triple.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? triple
            : throw new InvalidOperationException($"The host compiler does not report a native x86-64 Linux target: {compiler}.");
    }
}
