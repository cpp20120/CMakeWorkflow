using CMakeWorkflow.Host;
using CMakeWorkflow.Tooling;
using StreamJsonRpc;
using ClientSdk = CMakeWorkflow.Client;

internal static class ToolchainWireTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Toolchain: " + message);
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
        await using var sdk = await ClientSdk.CMakeWorkflowClient.ConnectAsync(new(endpoint, workspace), ct);
        var sdkInventory = await sdk.Toolchains.DiscoverAsync(ct);
        Check(sdkInventory.Candidates.Count == discovered.Candidates.Count, "SDK discovery differs from Host.");
        var sdkResolved = await sdk.Toolchains.ResolveAsync(new(), ct);
        Check(sdkResolved.Status == resolution.Status, "SDK resolution differs from Host.");
        Console.WriteLine("Toolchain discovery/resolver/SDK wire checks passed.");
    }
}
