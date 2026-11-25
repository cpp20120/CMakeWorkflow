using System.Security.Cryptography;
using CMakeWorkflow.Contracts.Workflows;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Host.Transport;
using StreamJsonRpc;

namespace CMakeWorkflow.Host.Tests.Transport;

internal static class ManifestPreparationWireTests
{
    internal static async Task RunAsync()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(30));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        using Stream wire = await LocalTransport.ConnectAsync(host.Endpoint, ct);
        using JsonRpc rpc = HostConnection.Create(wire);
        IHostRpc client = rpc.Attach<IHostRpc>();
        rpc.StartListening();
        _ = await client.InitializeAsync(new(1, host.Root), ct);

        byte[] bytes = """
            {
              "schemaVersion": 2,
              "contexts": { "native": { "buildDirectory": "build" } },
              "nodes": [{ "id": "inspect", "kind": "inspect", "context": "native" }]
            }
            """u8.ToArray();
        string digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        WorkflowManifestPreparation request = new(".", "results", digest, bytes);
        byte[] corrupt = "{}"u8.ToArray();
        await RefuseAsync(request with { ManifestBytes = corrupt }, "manifest_changed");
        await RefuseAsync(request with
        {
            ManifestBytes = corrupt,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(corrupt))
        }, "invalid_data");
        await RefuseAsync(request with { ManifestBytes = new byte[WorkflowManifestPreparation.MaximumBytes + 1] },
            "invalid_argument");
        await RefuseAsync(request with { ManifestBase = "../outside" }, "path_not_allowed");
        await RefuseAsync(request with { ResultsDirectory = "../outside" }, "path_not_allowed");
        SshTransportTests.Check(!Directory.EnumerateFiles(host.Root, ".cmwf-*").Any(), "Refused delivery left a file.");

        WorkflowManifestPreparationReceipt prepared = await client.PrepareManifestAsync(request, ct);
        SshTransportTests.Check(prepared.ManifestSha256 == digest && Path.GetFileName(prepared.Manifest) ==
            ".cmwf-workflow-" + digest + ".json" && File.Exists(prepared.Manifest), "Unexpected publication receipt.");
        await File.AppendAllTextAsync(prepared.Manifest, " ", ct);
        await RefuseAsync(request, "manifest_changed");
        SshTransportTests.Check(!Directory.Exists(prepared.ResultsDirectory), "Manifest delivery created a results directory.");
        Console.WriteLine("Manifest preparation wire passed: invalid payloads, size, paths and existing-byte verification.");

        async Task RefuseAsync(WorkflowManifestPreparation input, string expected)
        {
            RemoteInvocationException error = await SshTransportTests.RejectAsync<RemoteInvocationException>(
                () => client.PrepareManifestAsync(input, ct));
            SshTransportTests.Check(error.DeserializedErrorData is ApplicationError refusal && refusal.Code == expected,
                $"Expected {expected}; got {error.ErrorCode} {error.Message}");
        }
    }
}
