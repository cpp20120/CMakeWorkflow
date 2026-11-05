using CMakeWorkflow.Client;
using StreamJsonRpc;
using static SshTransportTests;

internal static class SshWorkflowPathTests
{
    private static void Check(bool condition, string message) => SshTransportTests.Check(condition, message);

    internal static async Task RunAsync(TransportTestHost host, CancellationToken ct)
    {
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "paths", ct);
        using SshWorkflowConnection connection = new(host.Endpoint, fixture.Workspace);
        await using (CMakeWorkflowClient client = await connection.ConnectAsync(ct))
        {
            string outside = Path.Combine(host.Root, "outside.json");
            File.Copy(fixture.Request.Manifest, outside);
            string link = Path.Combine(fixture.Workspace, "escape.json");
            _ = File.CreateSymbolicLink(link, outside);
            foreach (string? manifest in new[] { "workflow.json", "C:\\remote\\workflow.json", outside, link,
                         Path.Combine(fixture.Workspace, "..", "outside.json"),
                         Path.Combine(fixture.Workspace, "missing.json") })
            {
                _ = await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                    fixture.Request with { Manifest = manifest }, ct));
            }
            foreach (string? results in new[] { "results", host.Root })
            {
                _ = await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                    fixture.Request with { ResultsDirectory = results }, ct));
            }

            _ = await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                fixture.Request with { ManifestSha256 = new string('0', 64) }, ct));
            _ = await RejectAsync<RemoteInvocationException>(() => client.Operations.FindAsync(fixture.Request.ClientRequestId, ct));
            Check(fixture.Effects().Length == 0, "Rejected path or hash dispatched work.");
        }
        connection.AssertExited();
        using SshWorkflowConnection missing = new(host.Endpoint, Path.Combine(host.Root, "missing workspace"));
        _ = await RejectAsync<RemoteInvocationException>(() => missing.ConnectAsync(ct));
        missing.AssertExited();
        Console.WriteLine("SSH paths passed: remote absolute paths, workspace boundary, symlink escape, missing files and stale hash.");
    }
}
