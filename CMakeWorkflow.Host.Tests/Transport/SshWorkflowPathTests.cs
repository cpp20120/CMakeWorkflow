using StreamJsonRpc;
using static SshTransportTests;

internal static class SshWorkflowPathTests
{
    private static void Check(bool condition, string message) => SshTransportTests.Check(condition, message);

    internal static async Task RunAsync(TransportTestHost host, CancellationToken ct)
    {
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "paths", ct);
        using var connection = new SshWorkflowConnection(host.Endpoint, fixture.Workspace);
        await using (var client = await connection.ConnectAsync(ct))
        {
            var outside = Path.Combine(host.Root, "outside.json");
            File.Copy(fixture.Request.Manifest, outside);
            var link = Path.Combine(fixture.Workspace, "escape.json");
            File.CreateSymbolicLink(link, outside);
            foreach (var manifest in new[] { "workflow.json", "C:\\remote\\workflow.json", outside, link,
                         Path.Combine(fixture.Workspace, "..", "outside.json"),
                         Path.Combine(fixture.Workspace, "missing.json") })
            {
                await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                    fixture.Request with { Manifest = manifest }, ct));
            }
            foreach (var results in new[] { "results", host.Root })
                await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                    fixture.Request with { ResultsDirectory = results }, ct));
            await RejectAsync<RemoteInvocationException>(() => client.Workflows.StartAsync(
                fixture.Request with { ManifestSha256 = new string('0', 64) }, ct));
            await RejectAsync<RemoteInvocationException>(() => client.Operations.FindAsync(fixture.Request.ClientRequestId, ct));
            Check(fixture.Effects().Length == 0, "Rejected path or hash dispatched work.");
        }
        connection.AssertExited();
        using var missing = new SshWorkflowConnection(host.Endpoint, Path.Combine(host.Root, "missing workspace"));
        await RejectAsync<RemoteInvocationException>(() => missing.ConnectAsync(ct));
        missing.AssertExited();
        Console.WriteLine("SSH paths passed: remote absolute paths, workspace boundary, symlink escape, missing files and stale hash.");
    }
}
