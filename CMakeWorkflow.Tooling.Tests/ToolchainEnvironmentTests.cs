using System.Text;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling.Tests;

internal static class ToolchainEnvironmentTests
{
    internal static async Task RunAsync(string root, CancellationToken ct)
    {
        EnvironmentRequirements request = ToolchainEnvironment.Native("Ninja Multi-Config", []);
        IReadOnlyList<ManagedComponent> catalog = ManagedComponentCatalog.Components;
        ToolchainEnvironmentLock baseline = Resolve(request, catalog);
        byte[] serialized = ToolchainEnvironment.Serialize(baseline);
        Check(serialized.AsSpan().SequenceEqual(ToolchainEnvironment.Serialize(ToolchainEnvironment.Validate(serialized))),
            "Canonical v2 lock does not round trip");
        Check(serialized.AsSpan().SequenceEqual(ToolchainEnvironment.Serialize(Resolve(request, catalog.Reverse().ToArray()))),
            "Catalog enumeration changed the resolved identity");
        byte[] tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(serialized).Replace("\"schemaVersion\": 2", "\"schemaVersion\": 9", StringComparison.Ordinal));
        Reject(() => ToolchainEnvironment.Validate(tampered));
        Reject(() => ToolchainEnvironment.Validate([.. serialized, (byte)'\n']));
        Reject(() => ToolchainEnvironment.Validate(Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(serialized).Replace(ManagedReleases.CMakeRelease.Sha256, new string('0', 64), StringComparison.Ordinal))));
        byte[] frame = BinaryEnvironmentLock.Encode(baseline);
        string nativeId = BinaryEnvironmentLock.Identity(frame);
        Check(frame.AsSpan().SequenceEqual(BinaryEnvironmentLock.Encode(BinaryEnvironmentLock.Decode(frame))) &&
            nativeId != ManagedEnvironmentIdentity.Hash(serialized), "Native lock and legacy approval identities overlapped.");
        Reject(() => BinaryEnvironmentLock.Decode(BinaryEnvironmentLock.Encode(
            baseline with { Target = baseline.Target with { Abi = "invalid" } })));
        Reject(() => BinaryEnvironmentLock.Decode(BinaryEnvironmentLock.Encode(baseline with
        {
            Components = [.. baseline.Components.Select(component => component.Id is "llvm" or "sysroot"
                ? component with { Distribution = component.Distribution with { Sha256 = new string('0', 64) } }
                : component)]
        })));

        ManagedComponent ninja = catalog.Single(item => item.Release.Id == "ninja");
        ManagedComponent alternate = ninja with
        {
            Release = ninja.Release with { Version = "0.0.1-fixture", Sha256 = new string('1', 64) },
            ProfileDefault = false
        };
        ToolchainEnvironmentLock older = Resolve(request with { Versions = [new("ninja", alternate.Release.Version)] }, [.. catalog, alternate]);
        Check(older.Components.Single(item => item.Id == "ninja").Version == alternate.Release.Version &&
              ManagedEnvironmentIdentity.Hash(ToolchainEnvironment.Serialize(older)) != ManagedEnvironmentIdentity.Hash(serialized),
            "Exact version did not select side-by-side release identity");
        Reject(() => ToolchainEnvironment.Validate(ToolchainEnvironment.Serialize(older)));
        Check(ToolchainEnvironment.Resolve(request, [alternate, .. catalog]).Lock?.Components
            .Single(item => item.Id == "ninja").Version == ninja.Release.Version, "Default drifted to an unrequested version");
        CheckFailure(request with { Versions = [new("ninja", alternate.Release.Version)] },
            [.. catalog, alternate with { Release = alternate.Release with { Sha256 = "bad" } }], "invalid-catalog-release");

        CheckFailure(request with { Versions = [new("ninja", "missing")] }, catalog, "component-unavailable");
        CheckFailure(request with { Versions = [new("ninja", "first"), new("ninja", "second")] }, catalog, "conflicting-requirements");
        CheckFailure(request with { Policy = ToolResolutionPolicy.AllowSystem }, catalog, "unsupported-profile-policy");
        CheckFailure(request with { Host = new("windows", "x64") }, catalog, "unsupported-host-target");
        CheckFailure(request with { Target = request.Target with { Architecture = "arm64" } }, catalog, "unsupported-host-target");
        ManagedComponent sdk = catalog.Single(item => item.Release.Id == "sysroot");
        ManagedComponent incompatible = sdk with { Release = sdk.Release with { Version = "fixture" } };
        CheckFailure(request with { Versions = [new("sysroot", "fixture")] }, [.. catalog, incompatible], "incompatible-companions");

        if (OperatingSystem.IsLinux())
        {
            string store = Path.Combine(root, "environment-tests");
            string? previous = System.Environment.GetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE");
            System.Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", store);
            try
            {
                string digest = ManagedEnvironmentIdentity.Hash(serialized);
                EnvironmentInventoryEntry missing = await ToolchainEnvironment.InspectAsync(store, ninja.Release, ct);
                Check(missing.Status == "missing" && !Directory.Exists(store), "Discovery modified a missing store");
                await Task.WhenAll(ManagedEnvironmentLocks.WriteAsync(serialized, digest, ct),
                    ManagedEnvironmentLocks.WriteAsync(serialized, digest, ct));
                Check(await ManagedEnvironmentLocks.ReadAsync(digest, ct) is not null, "Published v2 lock was not accepted");
                byte[] legacy = ManagedEnvironmentLocks.Serialize();
                string legacyDigest = ManagedEnvironmentIdentity.Hash(legacy);
                Check(legacyDigest == "b54bd747ee5ae8d6ac037f0e57c43024269bd284ac283af8184e3e0b400b57f5",
                    "Existing Linux Clang lock digest changed");
                await ManagedEnvironmentLocks.WriteAsync(legacy, legacyDigest, ct);
                Check(await ManagedEnvironmentLocks.ReadAsync(legacyDigest, ct) is null, "Legacy lock format changed");
                await Task.WhenAll(ManagedEnvironmentLocks.WriteBinaryAsync(frame, nativeId, ct),
                    ManagedEnvironmentLocks.WriteBinaryAsync(frame, nativeId, ct));
                Check(await ManagedEnvironmentLocks.ReadAsync(nativeId, ct) is not null &&
                    !File.Exists(Path.Combine(store, "manifests", nativeId + ".json")),
                    "Native lock resolution required a JSON lock.");
                string project = Directory.CreateDirectory(Path.Combine(root, "native-vcpkg-project")).FullName;
                await File.WriteAllTextAsync(Path.Combine(project, "vcpkg.json"), "{}", ct);
                VcpkgProjectPlan plan = (await VcpkgProjects.PlanAsync(project, ct)) with { EnvironmentSha256 = nativeId };
                string binaryDependency = BinaryVcpkgPlan.Identity(BinaryVcpkgPlan.Encode(plan));
                Check(binaryDependency != ManagedEnvironmentLocks.Hash(VcpkgProjects.Serialize(plan)),
                    "Legacy dependency approval implicitly authorized a native plan.");
                VcpkgProjectReceipt receipt = new(plan, new string('1', 64), new string('2', 64));
                Check(BinaryVcpkgDocuments.DecodeReceipt(BinaryVcpkgDocuments.EncodeReceipt(receipt)) == receipt,
                    "Native receipt lost the original approved plan.");
                string corrupt = ManagedToolStore.Location(store, ninja.Release);
                _ = Directory.CreateDirectory(corrupt);
                EnvironmentInventoryEntry damaged = await ToolchainEnvironment.InspectAsync(store, ninja.Release, ct);
                Check(damaged.Status == "corrupted", "Corrupted tree was reported missing or installed");
            }
            finally
            {
                System.Environment.SetEnvironmentVariable("CMAKEWORKFLOW_TOOL_STORE", previous);
            }
        }

        Console.WriteLine("Toolchain environment catalog, resolution, lock and inventory checks passed.");
    }

    private static ToolchainEnvironmentLock Resolve(EnvironmentRequirements request, IReadOnlyList<ManagedComponent> catalog)
    {
        (ToolchainEnvironmentLock? resolved, ToolchainDiagnostic? diagnostic) = ToolchainEnvironment.Resolve(request, catalog);
        return resolved ?? throw new InvalidDataException(diagnostic?.Message);
    }

    private static void CheckFailure(EnvironmentRequirements request, IReadOnlyList<ManagedComponent> catalog, string expected)
    {
        (ToolchainEnvironmentLock? resolved, ToolchainDiagnostic? diagnostic) = ToolchainEnvironment.Resolve(request, catalog);
        Check(resolved is null && diagnostic?.Code == expected, expected);
    }

    private static void Reject(Func<ToolchainEnvironmentLock> operation)
    {
        try
        {
            _ = operation();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException("Invalid lock was accepted.");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok)
        {
            throw new InvalidOperationException(message);
        }
    }
}
