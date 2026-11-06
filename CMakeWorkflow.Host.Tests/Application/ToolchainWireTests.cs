using System.Text.Json;
using CMakeWorkflow.Client;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Toolchains;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using CMakeWorkflow.Tooling.Toolchains;
using StreamJsonRpc;
using ClientSdk = CMakeWorkflow.Client;
using CMakeWorkflow.CMake.Serialization;

namespace CMakeWorkflow.Host.Tests.Application;

internal static class ToolchainWireTests
{
    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("Toolchain: " + message);
        }
    }

    private sealed class FakeToolchainProvider(WorkflowToolchainRuntime runtime) : IWorkflowToolchainProvider
    {
        public int Calls { get; private set; }

        public Task<WorkflowToolchainRuntime> ResolveAsync(WorkflowToolchainBinding binding, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(runtime);
        }
    }

    // This is a real cmd.exe + batch execution smoke, deliberately independent
    // of the Visual Studio installations present on the CI runner. A path with
    // spaces exercises /s /c quoting, and `&& set` requires CALL to the batch.
    private static async Task CheckWindowsDeveloperCommandAsync(string workspace, CancellationToken ct)
    {
        string directory = Path.Combine(workspace, "developer command with spaces " + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(directory);
        string script = Path.Combine(directory, "VsDevCmd.bat");
        string argumentCapture = Path.Combine(directory, "received-arguments.txt");
        VisualStudioInstance fakeVs = new("fake", "Fake VS", "99.0", directory, script);
        ToolchainInventory inventory = new(1, "windows", [fakeVs], [], []);
        DeveloperEnvironmentRequest request = new("vs:fake", "x64", "x64", "10.0.26100.0", "14.51.36231");
        try
        {
            // Raw multiline literals may already contain CRLF after Windows checkout.
            // Normalize before converting to batch CRLF; avoid producing CRCRLF.
            await File.WriteAllTextAsync(script, """
                                                 @echo off
                                                 rem cmd.exe treats '=' as a separator in positional batch arguments.
                                                 rem %~2 therefore contains '-arch', not '-arch=x64'. Capture
                                                 rem %* instead: it preserves the original option syntax.
                                                 > "%~dp0received-arguments.txt" echo %*
                                                 set "VSCMD_VER=smoke"
                                                 set "VCToolsInstallDir=C:\Fake\MSVC\"
                                                 exit /b 0
                                                 """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), ct);
            DeveloperEnvironmentSnapshot snapshot = await WindowsToolchains.InspectEnvironmentAsync(inventory, request, ct);
            const string expectedArguments =
                "-no_logo -arch=x64 -host_arch=x64 -winsdk=10.0.26100.0 -vcvars_ver=14.51.36231";
            string capturedArguments = (await File.ReadAllTextAsync(argumentCapture, ct)).Trim();
            Check(string.Equals(capturedArguments, expectedArguments, StringComparison.Ordinal),
                $"cmd.exe changed VsDevCmd arguments: received [{capturedArguments}]; expected [{expectedArguments}].");
            Check(snapshot.VisibleVariables.GetValueOrDefault("VSCMD_VER") == "smoke"
                  && snapshot.VisibleVariables.GetValueOrDefault("VCToolsInstallDir") == @"C:\Fake\MSVC\"
                  && !string.IsNullOrWhiteSpace(snapshot.Fingerprint),
                "cmd.exe quoted VsDevCmd activation did not return to `set` or lost pinned versions.");

            await File.WriteAllTextAsync(script, """
                                                 @echo off
                                                 echo [ERROR:smoke] Expected diagnostic from failing batch 1>&2
                                                 exit /b 19
                                                 """.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal), ct);
            try
            {
                _ = await WindowsToolchains.InspectEnvironmentAsync(inventory, request, ct);
                throw new System.Diagnostics.UnreachableException("A failed VsDevCmd was accepted.");
            }
            catch (InvalidOperationException error)
            {
                Check(error.Message.Contains("exit 19", StringComparison.Ordinal)
                      && error.Message.Contains("[ERROR:smoke]", StringComparison.Ordinal)
                      && !error.Message.Contains("PATH=", StringComparison.OrdinalIgnoreCase),
                    "VsDevCmd failure lost diagnostics or exposed environment variables.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    public static async Task RunAsync(string endpoint, string workspace, CancellationToken ct)
    {
        // Pure resolver checks run on Linux/macOS CI too; no VS installation required.
        ToolchainCandidate cmake = new("cmake-path", "cmake", "cmake", "path", @"C:\tools\cmake.exe");
        ToolchainCandidate ninja = new("ninja-path", "ninja", "ninja", "path", @"C:\tools\ninja.exe");
        ToolchainCandidate compiler = new("msvc-vs1", "compiler", "msvc", "visual-studio",
            @"C:\VS1\cl.exe", "14.40", "one", "vs:one");
        ToolchainCandidate sdk = new("sdk-10", "sdk", "windows-sdk", "windows-sdk",
            @"C:\Kits\Include\10.0", "10.0");
        VisualStudioInstance one = new("one", "VS One", "17.0", @"C:\VS1", @"C:\VS1\VsDevCmd.bat");
        VisualStudioInstance two = new("two", "VS Two", "17.0", @"C:\VS2", @"C:\VS2\VsDevCmd.bat");
        ToolchainInventory inventory = new(1, "windows", [one, two], [cmake, ninja, compiler, sdk], []);
        ToolchainResolution chosen = WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: compiler.Id,
            SdkCandidateId: sdk.Id));
        Check(chosen.Status == "resolved" && chosen.EnvironmentProfileId == "vs:one"
                                          && chosen.CMake?.Provider == "path" && chosen.Ninja?.Provider == "path",
            "Resolver must combine standalone tools with MSVC developer environment.");
        // PATH order must not promote MinGW or an unconfigured LLVM install
        // ahead of an MSVC installation with a known developer environment.
        ToolchainCandidate mingw = new("mingw-g++", "compiler", "gcc", "path",
            @"C:\mingw64\bin\g++.exe");
        ToolchainCandidate looseClang = new("loose-clang", "compiler", "clang", "path",
            @"C:\tools\llvm\clang++.exe");
        ToolchainInventory mixed = inventory with { Candidates = [cmake, ninja, mingw, looseClang, compiler, sdk] };
        ToolchainResolution automatic = WindowsToolchains.Resolve(mixed, new());
        Check(automatic.Status == "resolved" && automatic.Compiler?.Id == compiler.Id,
            "MinGW/PATH must never override an activated MSVC-ABI toolchain.");
        ToolchainResolution onlyGnu = WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, mingw] }, new());
        Check(onlyGnu.Status == "incomplete" && onlyGnu.Compiler is null,
            "GNU-only Windows PATH must not satisfy automatic toolchain resolution.");
        ToolchainResolution explicitGnu = WindowsToolchains.Resolve(mixed, new(CompilerCandidateId: mingw.Id));
        Check(explicitGnu.Status == "resolved" && explicitGnu.Compiler?.Id == mingw.Id
                                               && explicitGnu.Sdk is null && explicitGnu.EnvironmentProfileId is null,
            "Explicit GNU toolchain must remain separate from VS/Windows SDK.");
        ToolchainResolution bareClang =
            WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, looseClang, sdk] }, new());
        Check(bareClang.Status == "incomplete", "Unactivated Windows clang++ must not be treated as configured.");
        ToolchainResolution missingSdk = WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, compiler] },
            new(CompilerCandidateId: compiler.Id));
        Check(missingSdk.Status == "incomplete", "MSVC ABI without Windows SDK must not be ready.");
        try
        {
            _ = WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: compiler.Id,
                EnvironmentProfileId: "vs:two"));
            throw new System.Diagnostics.UnreachableException("Mixed VS instances were accepted.");
        }
        catch (ArgumentException)
        {
        }

        try
        {
            _ = WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: "arbitrary-client-path"));
            throw new System.Diagnostics.UnreachableException("Arbitrary candidate ID was accepted.");
        }
        catch (ArgumentException)
        {
        }

        Dictionary<string, string> visible =
            WindowsToolchains.ParseVisibleEnvironment(
                "SECRET_TOKEN=abc\r\nVSCMD_VER=17.0\r\nINCLUDE=C:\\include\r\nPATH=C:\\bin\r\n");
        Check(!visible.ContainsKey("SECRET_TOKEN") && visible.Count == 3,
            "Environment inspection exposed inherited secrets.");
        Check(WindowsToolchains.CandidateId("cmake", @"C:\Tools\CMake.exe") ==
              WindowsToolchains.CandidateId("cmake", @"c:\tools\cmake.exe"),
            "Candidate identities must be Windows-path case insensitive.");

        // The published JSON contract spells CMake as "cmake", rather than the
        // default camel-case transform of the .NET name CMakeCandidateId ("cMake").
        // Exercise the exact external JSON, not just generated roundtrips.
        const string selectionJson = """{"cmakeCandidateId":"cmake-path","compilerCandidateId":"msvc-vs1"}""";
        WorkflowToolchainBinding? fromManifest = JsonSerializer.Deserialize(selectionJson,
            WorkflowJson.Default.WorkflowToolchainBinding);
        Check(fromManifest?.CMakeCandidateId == cmake.Id &&
              fromManifest.CompilerCandidateId == compiler.Id,
            "Documented workflow cmakeCandidateId was rejected by strict JSON deserializer.");
        Check(JsonSerializer.Serialize(fromManifest, WorkflowJson.Default.WorkflowToolchainBinding)
                .Contains("\"cmakeCandidateId\"", StringComparison.Ordinal),
            "Workflow serializer emitted nonstandard CMake candidate property casing.");
        ToolchainSelection? fromHostSelection = JsonSerializer.Deserialize(selectionJson,
            HostJson.Default.ToolchainSelection);
        Check(fromHostSelection?.CMakeCandidateId == cmake.Id,
            "Host resolver did not accept documented cmakeCandidateId.");
        string clientSelection = JsonSerializer.Serialize(
            new ClientSdk.Toolchains.ToolchainSelection(CMakeCandidateId: cmake.Id),
            CamelCaseJson);
        string clientPreparation = JsonSerializer.Serialize(
            new WorkflowToolchainBinding(CMakeCandidateId: cmake.Id),
            CamelCaseJson);
        Check(clientSelection.Contains("\"cmakeCandidateId\"", StringComparison.Ordinal) &&
              clientPreparation.Contains("\"cmakeCandidateId\"", StringComparison.Ordinal),
            "Client SDK must use documented cmakeCandidateId for discovery and preparation.");

        // A selected toolchain is part of the manifest and checkpoint identity,
        // but its inherited environment must never be persisted in run.json.
        WorkflowToolchainBinding bound = new(
            CMakeCandidateId: cmake.Id, NinjaCandidateId: ninja.Id,
            CompilerCandidateId: compiler.Id, SdkCandidateId: sdk.Id,
            EnvironmentProfileId: "vs:one");
        WorkflowDocument manifest = new()
        {
            SchemaVersion = 2,
            Contexts =
                { ["main"] = new WorkflowContext { BuildDirectory = workspace, Toolchain = bound } },
            Nodes = [new WorkflowNode { Id = "inspect", Kind = "inspect", Context = "main" }]
        };
        WorkflowDocument? roundtrip = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(manifest, WorkflowJson.Default.WorkflowDocument),
            WorkflowJson.Default.WorkflowDocument);
        Check(roundtrip?.Contexts["main"].Toolchain == bound, "Workflow context lost toolchain binding.");
        WorkflowToolchainRuntime runtime = new(cmake.Path, ninja.Path, compiler.Path,
            sdk.Path, "vs:one", "Ninja", "environment-fingerprint",
            new Dictionary<string, string> { ["INCLUDE"] = "secret-not-in-persisted-identity" });
        Dictionary<string, WorkflowToolchainRuntime> resolved = new() { [workspace] = runtime };
        // The engine must materialize a single snapshot per physical build tree,
        // even if multiple workflow nodes share its context.
        WorkflowToolchainRuntime fakeRuntime = runtime with
        {
            CMake = Path.Combine(workspace, "cmake.exe"),
            Ninja = Path.Combine(workspace, "ninja.exe"),
            Compiler = Path.Combine(workspace, "cl.exe")
        };
        FakeToolchainProvider provider = new(fakeRuntime);
        RequestedBuildContext requestContext = new(workspace);
        ObservedBuildContext observedContext = new() { BuildDirectory = workspace };
        WorkflowPlan planned = new(Path.Combine(workspace, "toolchain-test.json"), "hash",
        [
            new PlannedNode(new WorkflowNode { Id = "first", Kind = "build" },
                new CMakeTargetRef(workspace, "first", null), workspace, [],
                requestContext, observedContext, [], bound),
            new PlannedNode(new WorkflowNode { Id = "second", Kind = "build" },
                new CMakeTargetRef(workspace, "second", null), workspace, [],
                requestContext, observedContext, [], bound)
        ]);
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> materialized = await WorkflowToolchainAdmission.ResolveAsync(planned, provider, ct);
        Check(provider.Calls == 1 && materialized.Count == 1,
            "One physical build tree created multiple toolchain environments.");
        List<WorkflowToolchainIdentity> identities = WorkflowToolchainAdmission.Identities(resolved);
        Check(identities.Single().Fingerprint == runtime.Fingerprint &&
              !JsonSerializer.Serialize(identities)
                  .Contains("secret-not-in-persisted-identity", StringComparison.Ordinal),
            "Execution identity leaked environment variables.");
        WorkflowToolchainAdmission.RequireSameIdentity(identities, resolved);
        try
        {
            WorkflowToolchainAdmission.RequireSameIdentity(identities,
                new Dictionary<string, WorkflowToolchainRuntime>
                { [workspace] = runtime with { Fingerprint = "changed" } });
            throw new System.Diagnostics.UnreachableException("Resume accepted changed toolchain fingerprint.");
        }
        catch (InvalidDataException)
        {
        }

        string oldCache = Path.Combine(workspace, "toolchain-verification-build");
        _ = Directory.CreateDirectory(oldCache);
        await File.WriteAllTextAsync(Path.Combine(oldCache, "CMakeCache.txt"),
            $"CMAKE_GENERATOR:INTERNAL=Ninja\nCMAKE_CXX_COMPILER:FILEPATH={compiler.Path}\nCMAKE_MAKE_PROGRAM:FILEPATH={ninja.Path}\n", CancellationToken.None);
        WorkflowToolchainAdmission.ValidateBuildTree(oldCache, runtime);
        try
        {
            WorkflowToolchainAdmission.ValidateBuildTree(oldCache,
                runtime with { Compiler = Path.Combine(oldCache, "other-compiler.exe") });
            throw new System.Diagnostics.UnreachableException("Configured build tree was silently rebound to another compiler.");
        }
        catch (InvalidOperationException)
        {
        }

        if (OperatingSystem.IsWindows())
        {
            await CheckWindowsDeveloperCommandAsync(workspace, ct);
        }

        using Stream wire = await LocalTransport.ConnectAsync(endpoint, ct);
        using JsonRpc rpc = HostConnection.Create(wire);
        IHostRpc host = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        HostInfo hello = await host.InitializeAsync(new(1, workspace), ct);
        Check(hello.Capabilities.Contains("toolchains/discover") &&
              hello.Capabilities.Contains("toolchains/resolve") &&
              hello.Capabilities.Contains("toolchains/inspectEnvironment"), "Missing new capabilities.");
        ToolchainInventory discovered = await host.ToolchainsDiscoverAsync(new(), ct);
        Check(discovered.SchemaVersion == 1, "Invalid inventory schema.");
        Check(discovered.Candidates.All(x => !string.IsNullOrWhiteSpace(x.Id) &&
                                             Path.IsPathFullyQualified(x.Path)),
            "Discovery returned unqualified candidates.");
        ToolchainResolution resolution = await host.ToolchainsResolveAsync(new(new()), ct);
        Check(resolution.SchemaVersion == 1, "Invalid resolution schema.");
        if (!OperatingSystem.IsWindows())
        {
            Check(discovered.Platform == "unsupported" && resolution.Status == "unsupported",
                "Non-Windows discovery incorrectly claimed Windows support.");
            try
            {
                _ = await host.ToolchainsInspectEnvironmentAsync(new(new("vs:unknown")), ct);
                throw new System.Diagnostics.UnreachableException("Non-Windows VsDevCmd unexpectedly succeeded.");
            }
            catch (RemoteInvocationException ex)
            {
                Check(ex.ErrorCode == 1001 && ex.DeserializedErrorData is ApplicationError a
                                           && a.Code == "unsupported_platform", "Missing stable refusal code.");
            }
        }

        await using CMakeWorkflowClient sdkClient = await CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        ClientSdk.Toolchains.ToolchainInventory sdkInventory = await sdkClient.Toolchains.DiscoverAsync(ct);
        Check(sdkInventory.Candidates.Count == discovered.Candidates.Count, "SDK discovery differs from Host.");
        ClientSdk.Toolchains.ToolchainResolution sdkResolved = await sdkClient.Toolchains.ResolveAsync(new(), ct);
        Check(sdkResolved.Status == resolution.Status, "SDK resolution differs from Host.");
        Console.WriteLine("Toolchain discovery/resolver/SDK wire checks passed.");
    }
}
