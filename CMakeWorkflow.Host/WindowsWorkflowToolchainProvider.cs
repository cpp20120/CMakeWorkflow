using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Tooling;
using CMakeWorkflow;

namespace CMakeWorkflow.Host;

// Opt-in runtime resolver. Discovery is authoritative for candidate IDs; the
// requested environment is captured once per workflow (not once per node).
// None of the captured values ever enters the RPC protocol or persistent report.
public sealed class WindowsWorkflowToolchainProvider : IWorkflowToolchainProvider
{
    public async Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Explicit Windows toolchains require Windows.");
        // Discovery currently verifies only Hostx64/x64 MSVC and the x64 SDK libs.
        // A future architecture-aware candidate model must precede cross-target execution.
        if (binding.HostArchitecture != "x64" || binding.TargetArchitecture != "x64")
            throw new NotSupportedException("Executable toolchain binding currently supports x64 -> x64 only.");
        var inventory = await WindowsToolchains.DiscoverAsync(ct);
        var selected = WindowsToolchains.Resolve(inventory, new ToolchainSelection(
            binding.CMakeCandidateId, binding.NinjaCandidateId, binding.CompilerCandidateId,
            binding.SdkCandidateId, binding.EnvironmentProfileId, binding.Generator));
        if (selected.Status != "resolved" || selected.CMake is null || selected.Compiler is null)
            throw new InvalidOperationException("Cannot activate requested toolchain: " +
                                                string.Join("; ", selected.Diagnostics));
        // This execution adapter supports Ninja. VS generators also need an
        // instance/architecture-specific project-system binding, not just PATH.
        if (binding.Generator is not ("Ninja" or "Ninja Multi-Config"))
            throw new NotSupportedException(
                "Visual Studio generator execution binding is not yet supported; use Ninja.");
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (selected.EnvironmentProfileId is not null)
        {
            var snapshot = await WindowsToolchains.InspectEnvironmentAsync(inventory,
                new DeveloperEnvironmentRequest(selected.EnvironmentProfileId,
                    binding.HostArchitecture, binding.TargetArchitecture,
                    selected.Sdk?.Version, selected.Compiler.Flavor == "msvc" ? selected.Compiler.Version : null), ct);
            foreach (var (key, value) in snapshot.VisibleVariables) variables[key] = value;
            if (selected.Sdk?.Version is { } sdkVersion &&
                (!variables.TryGetValue("WindowsSDKVersion", out var actualSdk) ||
                 !string.Equals(actualSdk.TrimEnd('\\', '/'), sdkVersion.TrimEnd('\\', '/'),
                     StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("VsDevCmd activated a different Windows SDK than selected.");
            if (selected.Compiler.Flavor == "msvc")
            {
                if (!variables.TryGetValue("VCToolsInstallDir", out var vcRoot) ||
                    !Path.GetFullPath(selected.Compiler.Path).StartsWith(
                        Path.GetFullPath(vcRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("VsDevCmd activated another MSVC toolset than selected.");
                if (!string.Equals(variables.GetValueOrDefault("VSCMD_ARG_TGT_ARCH"),
                        binding.TargetArchitecture, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("VsDevCmd target architecture mismatch.");
            }
        }
        else if (selected.Compiler.Flavor is "msvc" or "clang-cl" or "clang")
            throw new InvalidOperationException("MSVC ABI compilation needs a developer environment.");

        // Select exact executables. CMake is invoked by absolute path; selected
        // Ninja and compiler directories take precedence over ambient PATH.
        var dirs = new[]
            {
                Path.GetDirectoryName(selected.Compiler.Path),
                selected.Ninja is null ? null : Path.GetDirectoryName(selected.Ninja.Path),
                Path.GetDirectoryName(selected.CMake.Path)
            }
            .Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase);
        var inheritedPath = variables.TryGetValue("PATH", out var activatedPath)
            ? activatedPath
            : Environment.GetEnvironmentVariable("PATH") ?? "";
        variables["PATH"] = string.Join(Path.PathSeparator.ToString(), dirs.Append(inheritedPath));
        foreach (var executable in new[] { selected.CMake.Path, selected.Compiler.Path, selected.Ninja?.Path })
            if (executable is not null && !File.Exists(executable))
                throw new FileNotFoundException("Toolchain executable disappeared after discovery.", executable);
        // Bind exact tool executables, not just their installation paths. This
        // prevents a replaced binary from silently matching the same candidate ID.
        var executableHashes = new List<string>();
        foreach (var executable in new[] { selected.CMake.Path, selected.Compiler.Path, selected.Ninja?.Path })
            if (executable is not null)
                executableHashes.Add(await CommandExecution.Sha256Async(executable, ct));
        var fingerprintText = string.Join("\n", selected.CMake.Id, selected.Ninja?.Id,
            selected.Compiler.Id, selected.Sdk?.Id, selected.EnvironmentProfileId, binding.Generator,
            binding.HostArchitecture, binding.TargetArchitecture,
            string.Join(";", executableHashes),
            string.Join("\n", variables.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.Key.ToUpperInvariant() + "=" + x.Value)));
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintText)));
        return new(selected.CMake.Path, selected.Ninja?.Path, selected.Compiler.Path,
            selected.Sdk?.Path, selected.EnvironmentProfileId, binding.Generator,
            fingerprint, variables);
    }
}