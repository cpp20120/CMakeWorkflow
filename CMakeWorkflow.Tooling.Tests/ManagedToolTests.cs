using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling.Tests;

internal static class ManagedToolTests
{
    internal static async Task RunAsync(string root)
    {
        string store = Path.Combine(root, "managed");
        string? previous = System.Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
        System.Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", store);
        try
        {
            byte[] zip = ZipFixture();
            ManagedToolRelease release = new("ninja", "1.2.3", ManagedToolStore.Platform,
                "https://example.invalid/ninja.zip", Convert.ToHexStringLower(SHA256.HashData(zip)),
                Hash("d:bin\nf:1:bin/ninja\n"u8, "fixture"u8), null);
            using Downloads downloads = new(zip);
            using HttpClient client = new(downloads, disposeHandler: false);
            VerifiedCMakeInstaller installer = new(client);
            FakeRuntime system = new();
            system.Add("cmake", "cmake version 4.0.0");
            system.Add("ctest", "ctest version 4.0.0");
            system.Add("apt-get", "apt 2.0");
            system.Add("ninja", "1.12.1");
            ToolingService service = new(system, installer)
            {
                FindManagedRelease = tool => tool == "ninja" ? release : ToolCatalog.ManagedRelease(tool),
                ManagedArchitecture = Architecture.X64
            };
            ToolingPlan systemPlan = await PlanAsync(service, ToolResolutionPolicy.AllowSystem);
            Check(systemPlan.Ready && systemPlan.Tools.All(t => t.Origin == "system" && t.Sha256 is null),
                "AllowSystem did not select compatible system tools");
            system.Add("cmake", "cmake version 3.20.0");
            ToolingPlan olderSystem = await PlanAsync(service, ToolResolutionPolicy.AllowSystem);
            ToolStatus outdated = olderSystem.Tools.Single(t => t.Id == "cmake");
            Check(outdated.Resolution == "downloadable" && outdated.SystemResolution == "incompatible" &&
                  outdated.SystemVersion == "3.20.0", "Wrong version was not distinguished");
            ToolingPlan sdk = await service.PlanAsync(["native"], PackageManagerKind.None, false, null, default,
                resolutionPolicy: ToolResolutionPolicy.ManagedOnly);
            Check(sdk.Tools.Single(t => t.Id == "cxx").Resolution == "external-sdk", "Missing SDK was accepted");
            await RejectAsync<ArgumentException>(() => service.PlanAsync([], PackageManagerKind.Apt, true,
                null, default, resolutionPolicy: ToolResolutionPolicy.ManagedOnly));
            ToolingService unsupported = new(system, installer) { ManagedArchitecture = Architecture.Arm64 };
            await RejectAsync<PlatformNotSupportedException>(() => PlanAsync(unsupported, ToolResolutionPolicy.ManagedOnly));
            Check(system.Calls.All(c => c.Executable != "apt-get"), "Managed planning invoked a package manager");
            Check(!Directory.Exists(store) && downloads.Calls == 0, "Managed planning modified the store");

            Runtime runtime = new();
            ToolingService managed = new(runtime, installer)
            {
                FindManagedRelease = _ => release,
                ManagedArchitecture = Architecture.X64
            };
            ToolingPlan pinned = await managed.PlanAsync(["ninja"], PackageManagerKind.Auto, true, null, default,
                resolutionPolicy: ToolResolutionPolicy.ManagedOnly);
            Check(pinned.Steps.Single().Release == release && pinned.Tools.Single().Resolution == "downloadable",
                "Missing tool did not resolve to the pinned release");
            await RejectAsync<InvalidDataException>(() => managed.ExecuteAsync(
                pinned with { PackageManager = PackageManagerKind.Apt }, default));
            ToolingStep mutated = pinned.Steps[0] with { Release = release with { Sha256 = new('0', 64) } };
            await RejectAsync<InvalidDataException>(() => managed.ExecuteAsync(pinned with { Steps = [mutated] }, default));
            Check(downloads.Calls == 0, "A mutated approval acquired a release");

            ManagedToolStore storage = new(installer);
            ManagedToolRelease wrongChecksum = release with { Sha256 = new('0', 64) };
            await RejectAsync<InvalidDataException>(() => storage.InstallAsync(store, wrongChecksum, runtime, default));
            Check(runtime.Probes == 0 && !Directory.Exists(ManagedToolStore.Location(store, wrongChecksum)),
                "Unverified bytes executed or published");

            ToolingPlan formatter = await service.PlanAsync(["cmakefmt"], PackageManagerKind.None, false, null, default,
                resolutionPolicy: ToolResolutionPolicy.ManagedOnly);
            ManagedToolRelease pinnedFormatter = formatter.Steps.Single().Release ??
                throw new InvalidOperationException("Missing managed cmakefmt release.");
            int attempts = downloads.Calls;
            await RejectAsync<InvalidDataException>(() => storage.InstallAsync(store, pinnedFormatter, runtime, default));
            Check(downloads.Calls == attempts + 1 && runtime.Probes == 0 &&
                  !Directory.Exists(ManagedToolStore.Location(store, pinnedFormatter)),
                "Non-matching cmakefmt archive was executed or published");
            system.Add("cmakefmt", "cmakefmt 2.3.0");
            ToolingPlan incompatible = await service.PlanAsync(["cmakefmt"], PackageManagerKind.None, false, null, default,
                resolutionPolicy: ToolResolutionPolicy.AllowSystem);
            Check(incompatible.Steps.Single().Release == pinnedFormatter &&
                  incompatible.Tools.Single().SystemResolution == "incompatible", "Older cmakefmt was accepted");
            system.Add("cmakefmt", "cmakefmt 2.4.0");
            ToolingPlan compatible = await service.PlanAsync(["cmakefmt"], PackageManagerKind.None, false, null, default,
                resolutionPolicy: ToolResolutionPolicy.AllowSystem);
            Check(compatible.Ready && compatible.Tools.Single().Origin == "system", "Compatible cmakefmt was not selected");

            runtime.WrongVersion = true;
            await RejectAsync<InvalidDataException>(() => storage.InstallAsync(store, release, runtime, default));
            runtime.WrongVersion = false;
            runtime.FailFunctional = true;
            await RejectAsync<InvalidDataException>(() => storage.InstallAsync(store, release, runtime, default));
            runtime.FailFunctional = false;
            Check(!await ManagedToolStore.IsInstalledAsync(store, release, default), "Failed probe published a tool");

            downloads.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using CancellationTokenSource canceled = new();
            Task interrupted = storage.InstallAsync(store, release, runtime, canceled.Token);
            await downloads.Pause.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await canceled.CancelAsync();
            await RejectAsync<OperationCanceledException>(() => interrupted);
            downloads.Pause = null;
            Check(!Directory.Exists(Path.Combine(store, "staging", release.Sha256)), "Cancellation left a staging release");

            string abandoned = Path.Combine(store, "staging", release.Sha256);
            _ = Directory.CreateDirectory(abandoned);
            await File.WriteAllTextAsync(Path.Combine(abandoned, "partial"), "interrupted", CancellationToken.None);
            int before = downloads.Calls;
            downloads.Pause = new(TaskCreationOptions.RunContinuationsAsynchronously);
            downloads.Continue = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<ToolingResult> apply = managed.ExecuteAsync(pinned, default);
            await downloads.Pause.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Task concurrent = storage.InstallAsync(store, release, runtime, default);
            Check(!concurrent.IsCompleted, "Concurrent installation did not wait for the publication lock");
            downloads.Continue.SetResult();
            await concurrent.WaitAsync(TimeSpan.FromSeconds(10));
            Check((await apply).Success && downloads.Calls == before + 1,
                "Concurrent installations did not coalesce after publication");
            await storage.InstallAsync(store, release, runtime, default);
            Check(downloads.Calls == before + 1 && !Directory.Exists(abandoned), "Reinstall mutated a completed release");

            string binary = ManagedToolStore.Executable(ManagedToolStore.Location(store, release), "ninja");
            await File.WriteAllTextAsync(binary, "tampered", CancellationToken.None);
            await RejectAsync<InvalidDataException>(() => ManagedToolStore.IsInstalledAsync(store, release, default));
            await RejectAsync<InvalidDataException>(() => storage.InstallAsync(store, release, runtime, default));
            Check(downloads.Calls == before + 1, "Managed store silently overwrote a damaged published release");

            foreach ((string name, TarEntryType type) in new[]
                     {
                         ("release/../escape", TarEntryType.RegularFile),
                         ("/escape", TarEntryType.RegularFile),
                         ("release/link", TarEntryType.SymbolicLink),
                         ("release/duplicate", TarEntryType.RegularFile)
                     })
            {
                byte[] tar = TarFixture(name, type);
                using Downloads tarDownloads = new(tar);
                using HttpClient tarClient = new(tarDownloads, disposeHandler: false);
                ManagedToolStore tarStore = new(new VerifiedCMakeInstaller(tarClient));
                ManagedToolRelease malicious = release with
                {
                    Id = "cmake",
                    TarRoot = "release",
                    Sha256 = Convert.ToHexStringLower(SHA256.HashData(tar))
                };
                int probes = runtime.Probes;
                await RejectAsync<InvalidDataException>(() => tarStore.InstallAsync(store, malicious, runtime, default));
                Check(runtime.Probes == probes && !Directory.Exists(ManagedToolStore.Location(store, malicious)),
                    "Unsafe tar entries reached executable publication");
            }

            await CheckEnvironmentAndProjectPlansAsync(root);
            Console.WriteLine("Managed tooling checks passed.");
        }
        finally
        {
            System.Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", previous);
        }
    }

    private static async Task CheckEnvironmentAndProjectPlansAsync(string root)
    {
        byte[] environment = ManagedEnvironmentLocks.Serialize();
        string digest = ManagedEnvironmentLocks.Hash(environment);
        await RejectAsync<InvalidDataException>(() => ManagedEnvironmentLocks.VerifyAsync(digest, default));
        await ManagedEnvironmentLocks.WriteAsync(environment, digest, default);
        await ManagedEnvironmentLocks.VerifyAsync(digest, default);
        await ManagedEnvironmentLocks.WriteAsync(environment, digest, default);

        string project = Path.Combine(root, "vcpkg-project");
        _ = Directory.CreateDirectory(project);
        string manifest = Path.Combine(project, "vcpkg.json");
        string configuration = Path.Combine(project, "vcpkg-configuration.json");
        await File.WriteAllTextAsync(manifest, """{"dependencies":["expected-lite"]}""", CancellationToken.None);
        VcpkgProjectPlan original = await VcpkgProjects.PlanAsync(project, default);
        Check(original.EnvironmentSha256 == digest && original.Checkout.Version == ToolCatalog.VcpkgCommit &&
              original.ConfigurationSha256 is null, "Project plan lost pinned dependencies or SDK identity");
        await File.WriteAllTextAsync(configuration, """{"registries":[]}""", CancellationToken.None);
        VcpkgProjectPlan configured = await VcpkgProjects.PlanAsync(project, default);
        Check(configured.ConfigurationSha256 is not null &&
              ManagedEnvironmentLocks.Hash(VcpkgProjects.Serialize(configured)) !=
              ManagedEnvironmentLocks.Hash(VcpkgProjects.Serialize(original)), "Registry input did not affect approval");
        await RejectAsync<InvalidDataException>(() => VcpkgProjects.ResolveAsync(
            ManagedEnvironmentLocks.Hash(VcpkgProjects.Serialize(configured)), default));

        await File.WriteAllTextAsync(configuration,
            """{"default-registry":{"kind":"git","repository":"https://example.invalid/registry","baseline":"main"}}""",
            CancellationToken.None);
        await RejectAsync<InvalidDataException>(() => VcpkgProjects.PlanAsync(project, default));

        string commit = new('a', 40);
        string snapshot = new('B', 64);
        File.Delete(configuration);
        await RejectAsync<InvalidDataException>(() => VcpkgProjects.PlanSnapshotAsync(project, commit, snapshot, default));
        string pinned = $$"""{"builtin-baseline":"{{ToolCatalog.VcpkgCommit}}","dependencies":["expected-lite"]}""";
        await File.WriteAllTextAsync(manifest, pinned, CancellationToken.None);
        VcpkgProjectPlan experiment = await VcpkgProjects.PlanSnapshotAsync(project, commit, snapshot, default);
        string relocated = Path.Combine(root, "relocated-project");
        _ = Directory.CreateDirectory(relocated);
        string relocatedManifest = Path.Combine(relocated, "vcpkg.json");
        await File.WriteAllTextAsync(relocatedManifest, pinned, CancellationToken.None);
        Check(experiment == await VcpkgProjects.PlanSnapshotAsync(relocated, commit, snapshot, default),
            "Experiment dependency digest depends on the staging path");
        await File.WriteAllTextAsync(relocatedManifest, pinned + "\n", CancellationToken.None);
        Check(experiment != await VcpkgProjects.PlanSnapshotAsync(relocated, commit, snapshot, default),
            "Experiment dependency digest ignored exact manifest bytes");
        await File.WriteAllTextAsync(manifest, """{"dependencies":["expected-lite"]}""", CancellationToken.None);
        await File.WriteAllTextAsync(configuration,
            $$$"""{"default-registry":{"kind":"git","repository":"https://example.invalid/registry","baseline":"{{{commit}}}","reference":"{{{commit}}}"}}""",
            CancellationToken.None);
        VcpkgProjectPlan gitRegistry = await VcpkgProjects.PlanSnapshotAsync(project, commit, snapshot, default);
        Check(gitRegistry.ConfigurationSha256 is not null && gitRegistry.TargetTriplet == VcpkgProjects.Triplet,
            "Experiment dependency plan lost its pinned registry or triplet");
        await File.WriteAllTextAsync(configuration,
            $$$"""{"default-registry":{"kind":"git","repository":"https://example.invalid/registry","baseline":"{{{commit}}}","reference":"main"}}""",
            CancellationToken.None);
        await RejectAsync<InvalidDataException>(() => VcpkgProjects.PlanSnapshotAsync(project, commit, snapshot, default));

        string path = Path.Combine(ManagedToolStore.GetRoot(), "manifests", digest + ".json");
        await File.WriteAllTextAsync(path, "tampered", CancellationToken.None);
        await RejectAsync<InvalidDataException>(() => ManagedEnvironmentLocks.VerifyAsync(digest, default));
    }

    private static Task<ToolingPlan> PlanAsync(ToolingService service, ToolResolutionPolicy policy) =>
        service.PlanAsync([], PackageManagerKind.Auto, true, null, default, resolutionPolicy: policy);

    private static void Check(bool condition, string text)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(text);
        }
    }

    private static async Task RejectAsync<T>(Func<Task> action) where T : Exception
    {
        try
        {
            await action();
        }
        catch (T)
        {
            return;
        }

        throw new System.Diagnostics.UnreachableException($"Expected {typeof(T).Name}");
    }

    private static string Hash(ReadOnlySpan<byte> prefix, ReadOnlySpan<byte> file)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(prefix);
        hash.AppendData(SHA256.HashData(file));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static byte[] ZipFixture()
    {
        using MemoryStream bytes = new();
        using (ZipArchive zip = new(bytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry ninja = zip.CreateEntry("ninja");
            ninja.ExternalAttributes = unchecked((int)0x81ed0000);
            using Stream entry = ninja.Open();
            entry.Write("fixture"u8);
        }

        return bytes.ToArray();
    }

    private static byte[] TarFixture(string name, TarEntryType type)
    {
        using MemoryStream bytes = new();
        using (GZipStream gzip = new(bytes, CompressionMode.Compress, leaveOpen: true))
        using (TarWriter tar = new(gzip))
        {
            PaxTarEntry entry = new(type, name);
            if (type == TarEntryType.SymbolicLink)
            {
                entry.LinkName = "../../escape";
            }

            tar.WriteEntry(entry);
            if (name.EndsWith("/duplicate", StringComparison.Ordinal))
            {
                tar.WriteEntry(new PaxTarEntry(type, name));
            }
        }

        return bytes.ToArray();
    }

    private sealed class Downloads(byte[] payload) : HttpMessageHandler
    {
        private int calls;
        internal int Calls => Volatile.Read(ref calls);
        internal TaskCompletionSource? Pause { get; set; }
        internal TaskCompletionSource? Continue { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref calls);
            if (Pause is not null)
            {
                _ = Pause.TrySetResult();
                await (Continue?.Task ?? Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)).WaitAsync(cancellationToken);
            }

            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
        }
    }

    private sealed class Runtime : IToolingRuntime
    {
        private int probes;
        internal int Probes => Volatile.Read(ref probes);
        internal bool WrongVersion { get; set; }
        internal bool FailFunctional { get; set; }
        public ToolPlatform Platform => ToolPlatform.Linux;
        public bool IsAdministrator => false;
        public string? Resolve(string command, string installDirectory) =>
            throw new System.Diagnostics.UnreachableException("ManagedOnly probed PATH.");

        public Task<ToolCommandResult> RunAsync(string executable, IReadOnlyList<string> arguments,
            TimeSpan timeout, CancellationToken ct)
        {
            Check(Path.GetFileName(executable) == "ninja", "Unexpected managed command");
            _ = Interlocked.Increment(ref probes);
            return Task.FromResult(arguments is ["--version"]
                ? new ToolCommandResult(0, WrongVersion ? "9.9.9" : "1.2.3", "")
                : new ToolCommandResult(FailFunctional ? 1 : 0, "tools", ""));
        }
    }
}
