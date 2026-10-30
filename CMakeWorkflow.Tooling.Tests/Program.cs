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
    // The native probe is explicitly opt-in; CI exercises only the ordinary user logger.
    if (OperatingSystem.IsWindows())
    {
        var etw = new NativeWindowsEtwProbe().Probe("user", default);
        Check(etw.Capability == "user" && etw.Status is "available" or "denied" or "busy" or "unverified" or "unsupported",
            "Windows ETW admission result shape");
        Console.WriteLine($"Windows ETW admission: {etw.Status} ({etw.Win32Error})");
    }
    // Native Windows API smoke: exercise PowrProf.dll, but never change the power scheme.
    if (OperatingSystem.IsWindows())
    {
        var nativePower = new WindowsPowerSettingsBackend();
        var currentScheme = nativePower.Read(WindowsHostSettingsProvisioner.SchemePath);
        Check(Guid.TryParseExact(currentScheme, "D", out _), "native Windows power scheme GUID");
        var noOp = WindowsHostSettingsProvisioner.Plan(
            new HostRequirements(1, ["native"], WindowsPowerScheme: currentScheme), nativePower);
        Check(noOp.Count == 0, "native Windows power scheme no-op plan");
        Check(nativePower.Read(WindowsHostSettingsProvisioner.SchemePath) == currentScheme,
            "native Windows smoke must not change the power scheme");
        try
        {
            nativePower.Read("windows/power/not-allowed");
            throw new Exception("native Windows backend accepted arbitrary setting");
        }
        catch (InvalidDataException) { }
        Console.WriteLine("Windows PowrProf native read/no-op smoke passed.");
    }
    var settingBackend = new FakeHostSettings();
    settingBackend.Values["/proc/sys/kernel/perf_event_paranoid"] = "3";
    settingBackend.Values["/sys/devices/system/cpu/cpufreq/policy0/scaling_governor"] = "powersave";
    var hostRequirements = new HostRequirements(1, ["native"], 1, "performance");
    var hostChanges = HostSettingsProvisioner.Plan(hostRequirements, settingBackend,
        ["/sys/devices/system/cpu/cpufreq/policy0/scaling_governor"]);
    Check(hostChanges.Count == 2 && settingBackend.Writes == 0, "host provisioner dry-run");
    settingBackend.Values["/proc/sys/kernel/perf_event_paranoid"] = "2";
    try { HostSettingsProvisioner.Apply(hostChanges, settingBackend, default); throw new Exception("host drift accepted"); }
    catch (InvalidOperationException) { }
    Check(settingBackend.Writes == 0, "preflight must reject stale plan without writes");
    settingBackend.Values["/proc/sys/kernel/perf_event_paranoid"] = "3";
    HostSettingsProvisioner.Apply(hostChanges, settingBackend, default);
    Check(settingBackend.Writes == 2 && settingBackend.Values["/proc/sys/kernel/perf_event_paranoid"] == "1",
        "typed host changes and recheck");
    try { HostSettingsProvisioner.Plan(hostRequirements, settingBackend, ["/tmp/evil/cpufreq/scaling_governor"]); throw new Exception("arbitrary path accepted"); }
    catch (InvalidDataException) { }
    // Two logical CPUs can share one cpufreq policy; provision only once.
    var shared = new FakeHostSettings();
    const string policy0 = "/sys/devices/system/cpu/cpufreq/policy0/scaling_governor";
    const string policy4 = "/sys/devices/system/cpu/cpufreq/policy4/scaling_governor";
    shared.Values[policy0] = "powersave";
    shared.Values[policy4] = "powersave";
    var sharedPlan = HostSettingsProvisioner.Plan(new HostRequirements(1, ["native"], CpuGovernor: "performance"),
        shared, [policy0, policy4]);
    Check(sharedPlan.Count == 2, "one write per cpufreq policy");
    HostSettingsProvisioner.Apply(sharedPlan, shared, default);
    Check(shared.Writes == 2 && shared.Values[policy0] == "performance" && shared.Values[policy4] == "performance",
        "shared policies applied exactly once");
    shared.Values["/proc/sys/kernel/perf_event_paranoid"] = "1";
    try { HostSettingsProvisioner.Plan(hostRequirements, shared,
        ["/sys/devices/system/cpu/cpu0/cpufreq/scaling_governor"]); throw new Exception("CPU alias accepted"); }
    catch (InvalidDataException) { }
    // A write failure after the first setting must remain observable as partial mutation.
    var partial = new FakeHostSettings();
    partial.Values["/proc/sys/kernel/perf_event_paranoid"] = "3";
    partial.Values[policy0] = "powersave";
    var partialPlan = HostSettingsProvisioner.Plan(hostRequirements, partial, [policy0]);
    partial.FailOnWrite = policy0;
    try { HostSettingsProvisioner.Apply(partialPlan, partial, default); throw new Exception("partial failure accepted"); }
    catch (IOException) { }
    Check(partial.Writes == 1 && partial.Values["/proc/sys/kernel/perf_event_paranoid"] == "1" &&
        partial.Values[policy0] == "powersave", "partial host change is not rolled back or hidden");
    // Windows development capabilities: toolchain and SDK are explicitly required, ETW access is not inferred from WPR.
    var fakeWindowsTools = new FakeWindowsDevelopmentProbe { Msvc = @"C:\vs\cl.exe", Sdk = @"C:\kits\10", Wpr = @"C:\wpr.exe" };
    var windowsToolRequirements = new HostRequirements(1, ["native"], RequireWindowsMsvc: true,
        RequireWindowsSdk: true, RequireWindowsWpr: true);
    var windowsToolFindings = WindowsDevelopmentDoctor.Inspect(windowsToolRequirements, fakeWindowsTools);
    Check(windowsToolFindings.Count == 4 && windowsToolFindings.Count(x => x.Status == "ready") == 3 &&
        windowsToolFindings.Single(x => x.Id == "windows_etw_permissions").Status == "unverified",
        "WPR discovery must not assert ETW permissions");
    fakeWindowsTools.Sdk = null;
    Check(WindowsDevelopmentDoctor.Inspect(windowsToolRequirements, fakeWindowsTools)
        .Single(x => x.Id == "windows_sdk").Status == "missing", "missing Windows SDK not reported");
    fakeWindowsTools.Sdk = @"C:\kits\10";
    // Fake Windows-only capability assessment is valid on Linux too.
    var winToolReqFile = Path.Combine(root, "windows-dev-requirements.json");
    File.WriteAllText(winToolReqFile,
        """{"schemaVersion":1,"toolProfiles":["native"],"requireWindowsMsvc":true,"requireWindowsSdk":true,"requireWindowsWpr":true}""");
    var winToolRuntime = new FakeRuntime();
    winToolRuntime.Add("cmake", "cmake version 4.4.4");
    winToolRuntime.Add("ctest", "ctest version 4.4.4");
    winToolRuntime.Add("c++", "g++ version 16.2.1");
    winToolRuntime.Add("ninja", "1.13.2");
    var winToolService = new ToolingService(winToolRuntime, new FakeInstaller(winToolRuntime));
    var winToolPlan = await HostPreparation.InspectAsync(winToolReqFile, winToolService,
        PackageManagerKind.None, null, default, windowsProbe: fakeWindowsTools);
    Check(!winToolPlan.Ready && winToolPlan.Findings.Any(x => x.Id == "windows_etw_permissions" && x.Status == "unverified"),
        "ETW must not be silently marked ready by a WPR executable");
    fakeWindowsTools.Msvc = null;
    var winToolStale = await HostPreparation.InspectAsync(winToolReqFile, winToolService,
        PackageManagerKind.None, null, default, windowsProbe: fakeWindowsTools);
    Check(winToolStale.ApprovalSha256 != winToolPlan.ApprovalSha256 &&
        winToolStale.Findings.Any(x => x.Id == "windows_msvc" && x.Status == "missing"),
        "Windows development capability drift must invalidate fingerprint");
    // Windows power scheme: validate the exact GUID, stale fingerprint, approval, audit and recheck.
    const string balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    const string highPerformance = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
    var windowsBackend = new FakeHostSettings();
    windowsBackend.Values[WindowsHostSettingsProvisioner.SchemePath] = balanced;
    var windowsRequirements = new HostRequirements(1, ["native"], WindowsPowerScheme: highPerformance);
    var windowsSettings = WindowsHostSettingsProvisioner.Plan(windowsRequirements, windowsBackend);
    Check(windowsSettings.Count == 1 && windowsSettings[0].Before == balanced &&
        windowsSettings[0].After == highPerformance && windowsBackend.Writes == 0, "Windows plan must be read only");
    var windowsRequirementsFile = Path.Combine(root, "windows-power-acceptance.json");
    File.WriteAllText(windowsRequirementsFile,
        """{"schemaVersion":1,"toolProfiles":["native"],"windowsPowerScheme":"8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c"}""");
    var windowsRuntime = new FakeRuntime();
    windowsRuntime.Add("cmake", "cmake version 4.4.4");
    windowsRuntime.Add("ctest", "ctest version 4.4.4");
    windowsRuntime.Add("c++", "g++ version 16.2.1");
    windowsRuntime.Add("ninja", "1.13.2");
    var windowsService = new ToolingService(windowsRuntime, new FakeInstaller(windowsRuntime));
    var windowsPlan = await HostPreparation.InspectAsync(windowsRequirementsFile, windowsService,
        PackageManagerKind.None, null, default, windowsBackend);
    Check(!windowsPlan.Ready && windowsPlan.SystemChanges?.Count == 1 &&
        windowsPlan.Findings.Single(x => x.Id == "windows_power_scheme").Status == "drift",
        "Windows power scheme drift reported");
    var windowsDenied = false;
    try { await HostPreparation.ApplyAsync(windowsPlan, windowsService, PackageManagerKind.None,
        null, default, backend: windowsBackend); }
    catch (InvalidOperationException) { windowsDenied = true; }
    Check(windowsDenied && windowsBackend.Writes == 0, "Windows system setting bypassed approval");
    windowsBackend.Values[WindowsHostSettingsProvisioner.SchemePath] = highPerformance;
    windowsDenied = false;
    try { await HostPreparation.ApplyAsync(windowsPlan, windowsService, PackageManagerKind.None,
        null, default, allowSystemChanges: true, backend: windowsBackend); }
    catch (InvalidOperationException) { windowsDenied = true; }
    Check(windowsDenied && windowsBackend.Writes == 0, "Windows stale plan accepted");
    windowsBackend.Values[WindowsHostSettingsProvisioner.SchemePath] = balanced;
    var windowsAudit = Path.Combine(root, "windows-audit");
    var windowsResult = await HostPreparation.ApplyAsync(windowsPlan, windowsService,
        PackageManagerKind.None, null, default, allowSystemChanges: true,
        backend: windowsBackend, auditDirectory: windowsAudit);
    Check(windowsResult.Ready && windowsBackend.Writes == 1 &&
        windowsBackend.Values[WindowsHostSettingsProvisioner.SchemePath] == highPerformance &&
        File.ReadAllLines(windowsResult.AuditPath!).Length == 3,
        "Windows scheme apply / audit / verification");
    var windowsInvalid = false;
    try { WindowsHostSettingsProvisioner.Apply(
        [new HostSettingChange("windows_power_scheme", "/tmp/arbitrary", balanced, highPerformance)],
        windowsBackend, default); }
    catch (InvalidDataException) { windowsInvalid = true; }
    Check(windowsInvalid, "Windows provisioner accepted arbitrary path");
    Console.WriteLine("Windows host provisioning checks passed: approval, stale state, audit, verification.");
    // End-to-end environment acceptance through the injectable sysfs backend.
    var acceptancePath = Path.Combine(root, "environment-acceptance.json");
    File.WriteAllText(acceptancePath,
        """{"schemaVersion":1,"toolProfiles":["native"],"maxPerfEventParanoid":1,"cpuGovernor":"performance"}""");
    var acceptanceRuntime = new FakeRuntime();
    acceptanceRuntime.Add("cmake", "cmake version 4.4.4");
    acceptanceRuntime.Add("ctest", "ctest version 4.4.4");
    acceptanceRuntime.Add("c++", "g++ version 16.2.1");
    acceptanceRuntime.Add("ninja", "1.13.2");
    var acceptanceService = new ToolingService(acceptanceRuntime, new FakeInstaller(acceptanceRuntime));
    var acceptanceBackend = new FakeHostSettings();
    acceptanceBackend.Values["/proc/sys/kernel/perf_event_paranoid"] = "2";
    acceptanceBackend.Values[policy0] = "powersave";
    var acceptancePaths = new[] { policy0 };
    var accepted = await HostPreparation.InspectAsync(acceptancePath, acceptanceService,
        PackageManagerKind.None, null, default, acceptanceBackend, acceptancePaths);
    Check(!accepted.Ready && accepted.Tools.Ready && accepted.SystemChanges?.Count == 2,
        "acceptance plan reports sysfs drift with ready tools");
    Check(acceptanceBackend.Writes == 0, "environment plan must be read-only");
    var denied = false;
    try { await HostPreparation.ApplyAsync(accepted, acceptanceService, PackageManagerKind.None,
        null, default, backend: acceptanceBackend, cpuPaths: acceptancePaths); }
    catch (InvalidOperationException) { denied = true; }
    Check(denied && acceptanceBackend.Writes == 0, "system write without approval accepted");
    acceptanceBackend.Values[policy0] = "schedutil";
    denied = false;
    try { await HostPreparation.ApplyAsync(accepted, acceptanceService, PackageManagerKind.None,
        null, default, allowSystemChanges: true, backend: acceptanceBackend, cpuPaths: acceptancePaths); }
    catch (InvalidOperationException) { denied = true; }
    Check(denied && acceptanceBackend.Writes == 0, "stale approval changed system state");
    acceptanceBackend.Values[policy0] = "powersave";
    var acceptanceAudit = Path.Combine(root, "acceptance-audit");
    var applied = await HostPreparation.ApplyAsync(accepted, acceptanceService, PackageManagerKind.None,
        null, default, allowSystemChanges: true, backend: acceptanceBackend,
        cpuPaths: acceptancePaths, auditDirectory: acceptanceAudit);
    Check(applied.Ready && applied.Checked.Findings.All(f => f.Status == "ready") &&
        acceptanceBackend.Writes == 2, "Apply did not recheck backend state");
    var auditLines = File.ReadAllLines(applied.AuditPath!);
    Check(auditLines.Length == 3 && auditLines[0].Contains("applying") &&
        auditLines[1].Contains("applied") && auditLines[2].Contains("verified"),
        "successful environment audit stages missing");
    // Independent partial-apply scenario must retain the first write and record failure.
    acceptanceBackend.Values["/proc/sys/kernel/perf_event_paranoid"] = "2";
    acceptanceBackend.Values[policy0] = "powersave";
    acceptanceBackend.FailOnWrite = policy0;
    var partialAccepted = await HostPreparation.InspectAsync(acceptancePath, acceptanceService,
        PackageManagerKind.None, null, default, acceptanceBackend, acceptancePaths);
    var partialFailed = false;
    try { await HostPreparation.ApplyAsync(partialAccepted, acceptanceService, PackageManagerKind.None,
        null, default, allowSystemChanges: true, backend: acceptanceBackend,
        cpuPaths: acceptancePaths, auditDirectory: acceptanceAudit); }
    catch (IOException) { partialFailed = true; }
    Check(partialFailed && acceptanceBackend.Values["/proc/sys/kernel/perf_event_paranoid"] == "1" &&
        acceptanceBackend.Values[policy0] == "powersave" &&
        File.ReadAllLines(applied.AuditPath!).Last().Contains("partial_or_failed"),
        "partial Apply was not preserved and audited");
    acceptanceBackend.FailOnWrite = null;
    Console.WriteLine("Environment acceptance checks passed: approval, drift, audit and recheck.");
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

sealed class FakeHostSettings : IHostSettingsBackend
{
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);
    public int Writes { get; private set; }
    public string? Read(string path) => Values.GetValueOrDefault(path);
    public string? FailOnWrite;
    public void Write(string path, string value) { if (path == FailOnWrite) throw new IOException("Simulated sysfs write failure"); Writes++; Values[path] = value; }
}
