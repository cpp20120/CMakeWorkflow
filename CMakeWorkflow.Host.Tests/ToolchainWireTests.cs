using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;
using ClientSdk = CMakeWorkflow.Client;
using CMakeWorkflow;
using System.Text.Json;

internal static class ToolchainWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Toolchain: " + message);
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
        var directory = Path.Combine(workspace, "developer command with spaces " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var script = Path.Combine(directory, "VsDevCmd.bat");
        var fakeVs = new VisualStudioInstance("fake", "Fake VS", "99.0", directory, script);
        var inventory = new ToolchainInventory(1, "windows", [fakeVs], [], []);
        var request = new DeveloperEnvironmentRequest("vs:fake", "x64", "x64", "10.0.26100.0", "14.51.36231");
        try
        {
            await File.WriteAllTextAsync(script, """
                @echo off
                if not "%~1"=="-no_logo" exit /b 31
                if not "%~2"=="-arch=x64" exit /b 32
                if not "%~3"=="-host_arch=x64" exit /b 33
                if not "%~4"=="-winsdk=10.0.26100.0" exit /b 34
                if not "%~5"=="-vcvars_ver=14.51.36231" exit /b 35
                set "VSCMD_VER=smoke"
                set "VCToolsInstallDir=C:\Fake\MSVC\"
                exit /b 0
                """.Replace("\n", "\r\n"), ct);
            var snapshot = await WindowsToolchains.InspectEnvironmentAsync(inventory, request, ct);
            Check(snapshot.VisibleVariables.GetValueOrDefault("VSCMD_VER") == "smoke"
                && snapshot.VisibleVariables.GetValueOrDefault("VCToolsInstallDir") == @"C:\Fake\MSVC\"
                && !string.IsNullOrWhiteSpace(snapshot.Fingerprint),
                "cmd.exe quoted VsDevCmd activation did not return to `set` or lost pinned versions.");

            await File.WriteAllTextAsync(script, """
                @echo off
                echo [ERROR:smoke] Expected diagnostic from failing batch 1>&2
                exit /b 19
                """.Replace("\n", "\r\n"), ct);
            try
            {
                await WindowsToolchains.InspectEnvironmentAsync(inventory, request, ct);
                throw new Exception("A failed VsDevCmd was accepted.");
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
        var cmake = new ToolchainCandidate("cmake-path", "cmake", "cmake", "path", @"C:\tools\cmake.exe");
        var ninja = new ToolchainCandidate("ninja-path", "ninja", "ninja", "path", @"C:\tools\ninja.exe");
        var compiler = new ToolchainCandidate("msvc-vs1", "compiler", "msvc", "visual-studio",
            @"C:\VS1\cl.exe", "14.40", "one", "vs:one");
        var sdk = new ToolchainCandidate("sdk-10", "sdk", "windows-sdk", "windows-sdk",
            @"C:\Kits\Include\10.0", "10.0");
        var one = new VisualStudioInstance("one", "VS One", "17.0", @"C:\VS1", @"C:\VS1\VsDevCmd.bat");
        var two = new VisualStudioInstance("two", "VS Two", "17.0", @"C:\VS2", @"C:\VS2\VsDevCmd.bat");
        var inventory = new ToolchainInventory(1, "windows", [one, two], [cmake, ninja, compiler, sdk], []);
        var chosen = WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: compiler.Id,
            SdkCandidateId: sdk.Id));
        Check(chosen.Status == "resolved" && chosen.EnvironmentProfileId == "vs:one"
            && chosen.CMake?.Provider == "path" && chosen.Ninja?.Provider == "path",
            "Resolver must combine standalone tools with MSVC developer environment.");
        // PATH order must not promote MinGW or an unconfigured LLVM install
        // ahead of an MSVC installation with a known developer environment.
        var mingw = new ToolchainCandidate("mingw-g++", "compiler", "gcc", "path",
            @"C:\mingw64\bin\g++.exe");
        var looseClang = new ToolchainCandidate("loose-clang", "compiler", "clang", "path",
            @"C:\tools\llvm\clang++.exe");
        var mixed = inventory with { Candidates = [cmake, ninja, mingw, looseClang, compiler, sdk] };
        var automatic = WindowsToolchains.Resolve(mixed, new());
        Check(automatic.Status == "resolved" && automatic.Compiler?.Id == compiler.Id,
            "MinGW/PATH must never override an activated MSVC-ABI toolchain.");
        var onlyGnu = WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, mingw] }, new());
        Check(onlyGnu.Status == "incomplete" && onlyGnu.Compiler is null,
            "GNU-only Windows PATH must not satisfy automatic toolchain resolution.");
        var explicitGnu = WindowsToolchains.Resolve(mixed, new(CompilerCandidateId: mingw.Id));
        Check(explicitGnu.Status == "resolved" && explicitGnu.Compiler?.Id == mingw.Id
            && explicitGnu.Sdk is null && explicitGnu.EnvironmentProfileId is null,
            "Explicit GNU toolchain must remain separate from VS/Windows SDK.");
        var bareClang = WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, looseClang, sdk] }, new());
        Check(bareClang.Status == "incomplete", "Unactivated Windows clang++ must not be treated as configured.");
        var missingSdk = WindowsToolchains.Resolve(inventory with { Candidates = [cmake, ninja, compiler] },
            new(CompilerCandidateId: compiler.Id));
        Check(missingSdk.Status == "incomplete", "MSVC ABI without Windows SDK must not be ready.");
        try
        {
            WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: compiler.Id,
                EnvironmentProfileId: "vs:two"));
            throw new Exception("Mixed VS instances were accepted.");
        }
        catch (ArgumentException) { }
        try
        {
            WindowsToolchains.Resolve(inventory, new(CompilerCandidateId: "arbitrary-client-path"));
            throw new Exception("Arbitrary candidate ID was accepted.");
        }
        catch (ArgumentException) { }
        var visible = WindowsToolchains.ParseVisibleEnvironment("SECRET_TOKEN=abc\r\nVSCMD_VER=17.0\r\nINCLUDE=C:\\include\r\nPATH=C:\\bin\r\n");
        Check(!visible.ContainsKey("SECRET_TOKEN") && visible.Count == 3,
            "Environment inspection exposed inherited secrets.");
        Check(WindowsToolchains.CandidateId("cmake", @"C:\Tools\CMake.exe") ==
              WindowsToolchains.CandidateId("cmake", @"c:\tools\cmake.exe"),
            "Candidate identities must be Windows-path case insensitive.");

        // The published JSON contract spells CMake as "cmake", rather than the
        // default camel-case transform of the .NET name CMakeCandidateId ("cMake").
        // Exercise the exact external JSON, not just generated roundtrips.
        const string selectionJson = """{"cmakeCandidateId":"cmake-path","compilerCandidateId":"msvc-vs1"}""";
        var fromManifest = JsonSerializer.Deserialize(selectionJson,
            WorkflowJson.Default.WorkflowToolchainBinding);
        Check(fromManifest?.CMakeCandidateId == cmake.Id &&
            fromManifest.CompilerCandidateId == compiler.Id,
            "Documented workflow cmakeCandidateId was rejected by strict JSON deserializer.");
        Check(JsonSerializer.Serialize(fromManifest, WorkflowJson.Default.WorkflowToolchainBinding)
                .Contains("\"cmakeCandidateId\"", StringComparison.Ordinal),
            "Workflow serializer emitted nonstandard CMake candidate property casing.");
        var fromHostSelection = JsonSerializer.Deserialize(selectionJson,
            HostJson.Default.ToolchainSelection);
        Check(fromHostSelection?.CMakeCandidateId == cmake.Id,
            "Host resolver did not accept documented cmakeCandidateId.");
        var clientSelection = JsonSerializer.Serialize(
            new ClientSdk.ToolchainSelection(CMakeCandidateId: cmake.Id),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var clientPreparation = JsonSerializer.Serialize(
            new ClientSdk.PrepareToolchainSelection(CMakeCandidateId: cmake.Id),
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Check(clientSelection.Contains("\"cmakeCandidateId\"", StringComparison.Ordinal) &&
            clientPreparation.Contains("\"cmakeCandidateId\"", StringComparison.Ordinal),
            "Client SDK must use documented cmakeCandidateId for discovery and preparation.");

        // A selected toolchain is part of the manifest and checkpoint identity,
        // but its inherited environment must never be persisted in run.json.
        var bound = new WorkflowToolchainBinding(
            CMakeCandidateId: cmake.Id, NinjaCandidateId: ninja.Id,
            CompilerCandidateId: compiler.Id, SdkCandidateId: sdk.Id,
            EnvironmentProfileId: "vs:one");
        var manifest = new WorkflowDocument { SchemaVersion = 2, Contexts =
            { ["main"] = new WorkflowContext { BuildDirectory = workspace, Toolchain = bound } },
            Nodes = [new WorkflowNode { Id = "inspect", Kind = "inspect", Context = "main" }] };
        var roundtrip = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(manifest, WorkflowJson.Default.WorkflowDocument),
            WorkflowJson.Default.WorkflowDocument);
        Check(roundtrip?.Contexts["main"].Toolchain == bound, "Workflow context lost toolchain binding.");
        var runtime = new WorkflowToolchainRuntime(cmake.Path, ninja.Path, compiler.Path,
            sdk.Path, "vs:one", "Ninja", "environment-fingerprint",
            new Dictionary<string, string> { ["INCLUDE"] = "secret-not-in-persisted-identity" });
        var resolved = new Dictionary<string, WorkflowToolchainRuntime> { [workspace] = runtime };
        // The engine must materialize a single snapshot per physical build tree,
        // even if multiple workflow nodes share its context.
        var fakeRuntime = runtime with
        {
            CMake = Path.Combine(workspace, "cmake.exe"),
            Ninja = Path.Combine(workspace, "ninja.exe"),
            Compiler = Path.Combine(workspace, "cl.exe")
        };
        var provider = new FakeToolchainProvider(fakeRuntime);
        var requestContext = new RequestedBuildContext(workspace);
        var observedContext = new ObservedBuildContext { BuildDirectory = workspace };
        var planned = new WorkflowPlan(Path.Combine(workspace, "toolchain-test.json"), "hash",
        [
            new PlannedNode(new WorkflowNode { Id = "first", Kind = "build" },
                new CMakeTargetRef(workspace, "first", null), workspace, [],
                requestContext, observedContext, [], bound),
            new PlannedNode(new WorkflowNode { Id = "second", Kind = "build" },
                new CMakeTargetRef(workspace, "second", null), workspace, [],
                requestContext, observedContext, [], bound)
        ]);
        var materialized = await WorkflowToolchainAdmission.ResolveAsync(planned, provider, ct);
        Check(provider.Calls == 1 && materialized.Count == 1,
            "One physical build tree created multiple toolchain environments.");
        var identities = WorkflowToolchainAdmission.Identities(resolved);
        Check(identities.Single().Fingerprint == runtime.Fingerprint &&
            !JsonSerializer.Serialize(identities).Contains("secret-not-in-persisted-identity", StringComparison.Ordinal),
            "Execution identity leaked environment variables.");
        WorkflowToolchainAdmission.RequireSameIdentity(identities, resolved);
        try
        {
            WorkflowToolchainAdmission.RequireSameIdentity(identities,
                new Dictionary<string, WorkflowToolchainRuntime> { [workspace] = runtime with { Fingerprint = "changed" } });
            throw new Exception("Resume accepted changed toolchain fingerprint.");
        }
        catch (InvalidDataException) { }
        var oldCache = Path.Combine(workspace, "toolchain-verification-build");
        Directory.CreateDirectory(oldCache);
        File.WriteAllText(Path.Combine(oldCache, "CMakeCache.txt"),
            $"CMAKE_GENERATOR:INTERNAL=Ninja\nCMAKE_CXX_COMPILER:FILEPATH={compiler.Path}\nCMAKE_MAKE_PROGRAM:FILEPATH={ninja.Path}\n");
        WorkflowToolchainAdmission.ValidateBuildTree(oldCache, runtime);
        try
        {
            WorkflowToolchainAdmission.ValidateBuildTree(oldCache,
                runtime with { Compiler = Path.Combine(oldCache, "other-compiler.exe") });
            throw new Exception("Configured build tree was silently rebound to another compiler.");
        }
        catch (InvalidOperationException) { }

        if (OperatingSystem.IsWindows())
            await CheckWindowsDeveloperCommandAsync(workspace, ct);

        using var wire = await LocalTransport.ConnectAsync(endpoint, ct);
        using var rpc = HostConnection.Create(wire);
        var host = rpc.Attach<IHostRpc>(); rpc.StartListening();
        var hello = await host.InitializeAsync(new(1, workspace), ct);
        Check(hello.Capabilities.Contains("toolchains/discover") &&
              hello.Capabilities.Contains("toolchains/resolve") &&
              hello.Capabilities.Contains("toolchains/inspectEnvironment"), "Missing new capabilities.");
        var discovered = await host.ToolchainsDiscoverAsync(new(), ct);
        Check(discovered.SchemaVersion == 1, "Invalid inventory schema.");
        Check(discovered.Candidates.All(x => !string.IsNullOrWhiteSpace(x.Id) &&
            System.IO.Path.IsPathFullyQualified(x.Path)), "Discovery returned unqualified candidates.");
        var resolution = await host.ToolchainsResolveAsync(new(new()), ct);
        Check(resolution.SchemaVersion == 1, "Invalid resolution schema.");
        if (!OperatingSystem.IsWindows())
        {
            Check(discovered.Platform == "unsupported" && resolution.Status == "unsupported",
                "Non-Windows discovery incorrectly claimed Windows support.");
            try
            {
                await host.ToolchainsInspectEnvironmentAsync(new(new("vs:unknown")), ct);
                throw new Exception("Non-Windows VsDevCmd unexpectedly succeeded.");
            }
            catch (RemoteInvocationException ex)
            {
                Check(ex.ErrorCode == 1001 && ex.DeserializedErrorData is ApplicationError a
                    && a.Code == "unsupported_platform", "Missing stable refusal code.");
            }
        }
        await using var sdkClient = await ClientSdk.CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        var sdkInventory = await sdkClient.Toolchains.DiscoverAsync(ct);
        Check(sdkInventory.Candidates.Count == discovered.Candidates.Count, "SDK discovery differs from Host.");
        var sdkResolved = await sdkClient.Toolchains.ResolveAsync(new(), ct);
        Check(sdkResolved.Status == resolution.Status, "SDK resolution differs from Host.");
        Console.WriteLine("Toolchain discovery/resolver/SDK wire checks passed.");
    }
}
