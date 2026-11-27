using System.Security.Cryptography;
using System.Text;
using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Toolchains;
using Binding = Capnp.Generated.ExecutionBinding;

namespace CMakeWorkflow.Host.Workflows;

// A bounded immutable dispatch record. Secrets remain in the resolved runtime.
internal static class BinaryDispatchBinding
{
    internal static byte[] Encode(AcceptedWorkflow accepted, BinaryWorkflowInput input, WorkflowPlan plan,
        IReadOnlyDictionary<string, WorkflowToolchainRuntime> toolchains,
        IReadOnlyDictionary<string, CompilerCacheProfile> cache, string? sourceSnapshotId, bool documentPlan)
    {
        MessageBuilder message = new(new MessageLimits
        {
            MaxListElements = 16_384,
            MaxTextBytes = 16_384
        });
        Binding.DispatchBinding.Builder root = Binding.DispatchBinding.InitRoot(message);
        root.SetVersion(1);
        root.SetOperationId(accepted.OperationId);
        root.SetWorkspaceId(accepted.WorkspaceId);
        root.SetRequestDigest(accepted.RequestDigest);
        root.SetDocumentId(input.Document.Id.Value);
        root.SetBlobId(input.Document.Blob.Value);
        root.SetCanonicalDigest(input.Document.Canonical?.Value);
        root.SetManifestPath(input.ManifestPath);
        root.SetResultsDirectory(input.ResultsDirectory);
        root.SetJobs((uint)input.Jobs);
        root.SetSourceSnapshotId(sourceSnapshotId);
        Binding.DispatchBinding.NodesListBuilder nodes = root.InitNodes(plan.Nodes.Count);
        Dictionary<string, string> environmentHashes = new(WorkflowPlanner.PathComparer);
        for (int i = 0; i < nodes.Count; i++)
        {
            PlannedNode node = plan.Nodes[i];
            Binding.NodeBinding.Builder value = nodes[i];
            value.SetId(node.Definition.Id);
            value.SetKind(node.Definition.Kind);
            value.SetTarget(node.Target.Target);
            value.SetBuildDirectory(node.Target.BuildDirectory);
            value.SetRequestedConfiguration(node.Requested.Configuration);
            value.SetWorkingDirectory(node.WorkingDirectory);
            value.SetSourceDirectory(node.Requested.SourceDirectory);
            value.SetConfigurePreset(node.Requested.ConfigurePreset);
            value.SetMode(node.Requested.Mode);
            value.SetGenerator(node.Requested.Generator);
            value.SetProvenancePolicy(node.Requested.ProvenancePolicy);
            if (node.CompilerCacheProfile is { } profile && cache.TryGetValue(profile, out CompilerCacheProfile? found))
            {
                value.SetCompilerCacheFingerprint(found.Fingerprint);
            }

            Binding.NodeBinding.DependenciesListBuilder dependencies = value.InitDependencies(node.Dependencies.Count);
            for (int j = 0; j < dependencies.Count; j++)
            {
                dependencies.Set(j, node.Dependencies[j]);
            }

            if (toolchains.TryGetValue(node.Target.BuildDirectory, out WorkflowToolchainRuntime? runtime))
            {
                if (!environmentHashes.TryGetValue(node.Target.BuildDirectory, out string? environment))
                {
                    environment = EnvironmentDigest(runtime.Variables);
                    environmentHashes.Add(node.Target.BuildDirectory, environment);
                }

                value.SetRuntimeFingerprint(runtime.Fingerprint);
                value.SetManagedEnvironmentSha256(environment);
                if (documentPlan)
                {
                    value.SetEnvironmentLockIdentity(runtime.Linux?.EnvironmentLockSha256);
                    value.SetDependencyPlanIdentity(runtime.Linux?.VcpkgProjectSha256);
                    value.SetDependencyReceiptIdentity(runtime.VcpkgReceiptIdentity);
                }
                if (runtime.Vcpkg is { } vcpkg)
                {
                    value.SetDependencyTriplet(vcpkg.Triplet);
                    value.SetDependencyManifestRoot(vcpkg.ManifestRoot);
                    value.SetDependencyInstalledDirectory(vcpkg.InstalledDirectory);
                    value.SetDependencyToolchain(vcpkg.VcpkgToolchain);
                    value.SetDependencyChainloadToolchain(vcpkg.ChainloadToolchain);
                }
            }
        }

        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static string Identity(ReadOnlySpan<byte> bytes, bool documentPlan = false)
    {
        using IncrementalHash digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        digest.AppendData(documentPlan
            ? "cmwf-cmake-dispatch-binding-v2\n"u8 : "cmwf-cmake-dispatch-binding-v1\n"u8);
        digest.AppendData(bytes);
        return Convert.ToHexStringLower(digest.GetHashAndReset());
    }

    private static string EnvironmentDigest(IReadOnlyDictionary<string, string> variables)
    {
        using MemoryStream bytes = new();
        using BinaryWriter writer = new(bytes, Encoding.UTF8, leaveOpen: true);
        foreach ((string name, string value) in variables.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.Write(name);
            writer.Write(value);
        }

        writer.Flush();
        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
    }
}
