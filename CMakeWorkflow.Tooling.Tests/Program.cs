using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using CMakeWorkflow.Tooling;

static void Check(bool condition, string name)
{ if (!condition) throw new Exception(name); }

var root = Path.Combine(Path.GetTempPath(), "cmake-workflow-tooling-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    Check(ToolCatalog.ResolveProfiles(["profiling"], ToolPlatform.Linux)
        .Any(x => x.Id == "perf"), "Linux profile backend");
    Check(ToolCatalog.ResolveProfiles(["profiling"], ToolPlatform.Windows)
        .Any(x => x.Id == "wpr"), "Windows profile backend");
    Check(ToolCatalog.ResolveProfiles(["profiling"], ToolPlatform.MacOS)
        .Any(x => x.Id == "xctrace"), "macOS profile backend");
    Check(ToolCatalog.ResolveProfiles(["native"], ToolPlatform.Linux)
        .Select(x => x.Id).Order(StringComparer.Ordinal)
        .SequenceEqual(["cmake", "ctest", "cxx", "ninja"]),
        "native workflow profile");
    Check(ToolCatalog.GetTool("cmake").MinimumVersion == new Version(3, 26),
        "CMake minimum version");
    Check(ToolingService.InstallCommand(PackageManagerKind.Winget, ["Kitware.CMake"])
        .Arguments.Contains("--accept-package-agreements"), "winget explicit agreements");
    Check(ToolingService.InstallCommand(PackageManagerKind.Brew, ["cmake"])
        .Arguments.SequenceEqual(["install", "cmake"]), "Homebrew package mapping");
    var install = Path.Combine(root, "bin");
    var runtime = new FakeRuntime();
    runtime.Add("apt-get", "apt 2.0");
    runtime.Add("sudo", "sudo 1.0");
    runtime.Add("c++", "GCC 13.2.0");
    var fallback = new FakeInstaller(runtime);
    var service = new ToolingService(runtime, fallback);
    var plan = await service.PlanAsync(["cmake"], PackageManagerKind.Apt, true, install, default);
    Check(!plan.Ready && plan.PackageManager == PackageManagerKind.Apt
        && plan.Steps.Count(x => x.Method == "package-index") == 1
        && plan.Steps.Any(x => x.Tool == "cmake" && x.Method == "verified-release"),
        "package-first plan with verified fallback");
    Check(runtime.Calls.All(x => x.Args is ["--version"]), "planning executed a mutation");
    runtime.PackageWorks = true;
    var packageResult = await service.ExecuteAsync(plan, default);
    Check(packageResult.Success && fallback.Calls == 0
        && runtime.Calls.Any(x => x.Executable == "apt-get" && x.Args.Contains("install")),
        "package installation and re-probe");

    var fallbackRuntime = new FakeRuntime();
    fallbackRuntime.Add("apt-get", "apt 2.0");
    fallbackRuntime.Add("c++", "GCC 13.2.0");
    var fallbackInstaller = new FakeInstaller(fallbackRuntime);
    var fallbackService = new ToolingService(fallbackRuntime, fallbackInstaller);
    var fallbackPlan = await fallbackService.PlanAsync(["cmake"], PackageManagerKind.Apt,
        true, install, default);
    var fallbackResult = await fallbackService.ExecuteAsync(fallbackPlan, default);
    Check(fallbackResult.Success && fallbackInstaller.Calls > 0,
        "failed package installation uses verified fallback");
    var noFallback = await fallbackService.PlanAsync(["profiling"], PackageManagerKind.None,
        false, install, default);
    Check(noFallback.Steps.Any(x => x.Tool == "perf" && x.Method == "manual")
        && noFallback.Steps.All(x => x.Method != "verified-release"),
        "no fallback and manual capability diagnostics");
    var noFallbackResult = await fallbackService.ExecuteAsync(noFallback, default);
    Check(!noFallbackResult.Success && noFallbackResult.Failed.Contains("perf"),
        "manual capability remains missing");
    Check(ToolingService.ExtractVersion("cmake version 3.31.2") == new Version(3,31,2),
        "version parser");

    if (OperatingSystem.IsLinux())
    {
        var architecture = RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? "aarch64" : "x86_64";
        var previousStore = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", Path.Combine(root, "store"));
        try
        {
            var archive = Archive(architecture);
            var digest = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant();
            using var client = new HttpClient(new ReleaseHandler(archive, digest, architecture));
            await new VerifiedCMakeInstaller(client).InstallAsync(Path.Combine(root, "verified-bin"), default);
            Check(File.Exists(Path.Combine(root, "verified-bin", "cmake"))
                && File.Exists(Path.Combine(root, "verified-bin", "ctest")),
                "verified release extraction");
            var collision = Path.Combine(root, "collision-bin");
            Directory.CreateDirectory(collision);
            File.WriteAllText(Path.Combine(collision, "ctest"), "existing");
            try
            {
                await new VerifiedCMakeInstaller(client).InstallAsync(collision, default);
                throw new Exception("unrelated executable was replaced");
            }
            catch (IOException error) when (error.Message.Contains("unrelated executable")) { }
            Check(File.ReadAllText(Path.Combine(collision, "ctest")) == "existing"
                && !File.Exists(Path.Combine(collision, "cmake")),
                "collision preflight avoids partial links");
            using var badClient = new HttpClient(new ReleaseHandler(archive, new string('0', 64), architecture));
            try
            {
                await new VerifiedCMakeInstaller(badClient).InstallAsync(Path.Combine(root, "bad-bin"), default);
                throw new Exception("tampered digest was accepted");
            }
            catch (InvalidDataException error) when (error.Message.Contains("SHA-256")) { }
            Check(!Directory.Exists(Path.Combine(root, "bad-bin")), "unverified files were installed");
        }
        finally { Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", previousStore); }
    }
    Console.WriteLine("Tooling checks passed.");
}
finally { Directory.Delete(root, recursive: true); }

static byte[] Archive(string architecture)
{
    using var bytes = new MemoryStream();
    using (var gzip = new GZipStream(bytes, CompressionLevel.Fastest, leaveOpen: true))
    using (var tar = new TarWriter(gzip, leaveOpen: true))
        foreach (var name in new[] { "cmake", "ctest", "cpack" })
        {
            var entry = new PaxTarEntry(TarEntryType.RegularFile,
                $"cmake-3.31.0-linux-{architecture}/bin/" + name)
            { DataStream = new MemoryStream(Encoding.ASCII.GetBytes("fixture")) };
            tar.WriteEntry(entry);
        }
    return bytes.ToArray();
}

sealed class FakeRuntime : IToolingRuntime
{
    private readonly Dictionary<string, string> versions = new(StringComparer.Ordinal);
    public ToolPlatform Platform => ToolPlatform.Linux;
    public bool IsAdministrator => true;
    public bool PackageWorks { get; set; }
    public List<(string Executable, string[] Args)> Calls { get; } = [];
    public void Add(string name, string version) => versions[name] = version;
    public string? Resolve(string command, string installDirectory) =>
        versions.ContainsKey(command) ? command : null;
    public Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
        TimeSpan timeout, CancellationToken ct)
    {
        Calls.Add((executable, arguments.ToArray()));
        if (executable == "apt-get")
        {
            if (arguments.Contains("install") && PackageWorks)
            { Add("cmake", "cmake version 3.31.0"); Add("ctest", "ctest version 3.31.0"); }
            return Task.FromResult(new ToolCommandResult(PackageWorks ? 0 : 1, "", "package unavailable"));
        }
        return Task.FromResult(new ToolCommandResult(0, versions.GetValueOrDefault(executable, ""), ""));
    }
}

sealed class FakeInstaller(FakeRuntime runtime) : ICMakeInstaller
{
    public int Calls { get; private set; }
    public Task InstallAsync(string installDirectory, CancellationToken ct)
    {
        Calls++;
        runtime.Add("cmake", "cmake version 3.31.0");
        runtime.Add("ctest", "ctest version 3.31.0");
        return Task.CompletedTask;
    }
}

sealed class ReleaseHandler(byte[] archive, string digest, string architecture) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK);
        if (request.RequestUri!.Host == "api.github.com")
        {
            var json = JsonSerializer.Serialize(new
            {
                tag_name = "v3.31.0",
                assets = new[] { new { name = $"cmake-3.31.0-linux-{architecture}.tar.gz",
                    browser_download_url = $"https://github.com/Kitware/CMake/releases/download/v3.31.0/cmake-3.31.0-linux-{architecture}.tar.gz",
                    digest = "sha256:" + digest } }
            });
            response.Content = new StringContent(json);
        }
        else response.Content = new ByteArrayContent(archive);
        return Task.FromResult(response);
    }
}
