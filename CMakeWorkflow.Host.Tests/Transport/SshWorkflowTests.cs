using CMakeWorkflow.Client;
using StreamJsonRpc;
using static SshTransportTests;

internal static class SshWorkflowTests
{
    private static void Check(bool condition, string message) => SshTransportTests.Check(condition, message);

    internal static async Task RunAsync(string? scenario)
    {
        if (OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Remote workflow fixture requires Linux/POSIX, CMake and Ninja.");
        if (scenario is not (null or "execution" or "cancellation" or "paths" or "lost-reply"))
            throw new ArgumentException("Expected execution, cancellation, paths or lost-reply.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var ct = deadline.Token;
        await using var host = new TransportTestHost();
        await host.ReadyAsync(ct);
        if (scenario is null or "execution") await ExecuteAsync(host, ct);
        if (scenario is null or "cancellation") await CancelAsync(host, ct);
        if (scenario is null or "paths") await SshWorkflowPathTests.RunAsync(host, ct);
        if (scenario is null or "lost-reply") await LostReplyAsync(host, ct);
        Console.WriteLine("SSH durable workflow tests passed: " + (scenario ?? "execution, cancellation, paths, lost-reply") + ".");
    }

    private static async Task ExecuteAsync(TransportTestHost host, CancellationToken ct)
    {
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "execution", ct);
        using var first = new SshWorkflowConnection(host.Endpoint, fixture.Workspace);
        using var second = new SshWorkflowConnection(host.Endpoint, fixture.Workspace);
        string id;
        long cursor;
        await using (var client = await first.ConnectAsync(ct))
        {
            var operation = await client.Workflows.StartAsync(fixture.Request, ct);
            id = operation.OperationId;
            await fixture.WaitForStartAsync(ct);
            await RejectAsync<RemoteInvocationException>(() => operation.GetReportAsync(ct));
            var page = await operation.ReadEventsAsync(0, 1, ct);
            Check(page.Events.Count == 1 && page.NextSequence > 0, "No live event cursor.");
            cursor = page.NextSequence;
            await using var other = await second.ConnectAsync(ct);
            var retry = await other.Workflows.StartAsync(fixture.Request, ct);
            Check(retry.OperationId == id, "Concurrent client duplicated durable acceptance.");
            await RejectAsync<RemoteInvocationException>(() => other.Workflows.StartAsync(
                fixture.Request with { Jobs = 2 }, ct));
        }
        first.AssertExited();
        second.AssertExited();
        Check(fixture.Effects().SequenceEqual(["started"]), "Disconnect canceled or completed the gated effect.");

        // An explicitly identical retry must retain the accepted immutable snapshot.
        File.WriteAllText(fixture.Request.Manifest, "invalid replacement after acceptance");
        await using (var reconnected = await second.ConnectAsync(ct))
        {
            Check(reconnected.Host.ProcessId == Environment.ProcessId, "Reconnection activated another Host.");
            var found = await reconnected.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == id && found.ManifestSha256 == fixture.Request.ManifestSha256 &&
                  found.ManifestBaseDirectory == fixture.Workspace, "Recovered acceptance identity changed.");
            var operation = reconnected.Operations.GetHandle(found.OperationId);
            fixture.Finish();
            var final = await operation.WaitForCompletionAsync(ct: ct);
            Check(final.State == "succeeded", "Remote workflow failed: " + final.State);
            var report = await operation.GetReportAsync(ct);
            Check(report.SchemaVersion == 1 && report.Revision.Length == 64 &&
                  report.Report.GetProperty("status").GetString() == "succeeded" &&
                  report.RunDirectory == fixture.Request.ResultsDirectory,
                "Operation-bound terminal report is incorrect.");
            var recovery = await operation.InspectRecoveryAsync(ct);
            Check(recovery.TerminalState == "succeeded" && !recovery.CancellationRequested,
                "Disconnect changed durable recovery state.");
            await VerifyReplayAsync(operation, cursor, ct);
            var retry = await reconnected.Workflows.StartAsync(fixture.Request, ct);
            Check(retry.OperationId == id, "Terminal retry created a new operation.");
            Check(fixture.Effects().SequenceEqual(["started", "finished"]), "Remote effect ran more than once.");
            // A post-finalization edit must not replace the report bound to this operation.
            File.AppendAllText(Path.Combine(fixture.Request.ResultsDirectory, "run.json"), " ");
            await RejectAsync<RemoteInvocationException>(() => operation.GetReportAsync(ct));
        }
        second.AssertExited();
        Console.WriteLine("SSH execution passed: two clients, disconnect/reconnect, request identity, immutable snapshot, cursor replay and one effect.");
    }

    private static async Task CancelAsync(TransportTestHost host, CancellationToken ct)
    {
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "cancellation", ct);
        using var connection = new SshWorkflowConnection(host.Endpoint, fixture.Workspace);
        string id;
        await using (var client = await connection.ConnectAsync(ct))
        {
            id = (await client.Workflows.StartAsync(fixture.Request, ct)).OperationId;
            await fixture.WaitForStartAsync(ct);
        }
        connection.AssertExited();
        await using (var client = await connection.ConnectAsync(ct))
        {
            var operation = client.Operations.GetHandle(id);
            var recovery = await operation.InspectRecoveryAsync(ct);
            await RejectAsync<RemoteInvocationException>(() => operation.RequestCancellationAsync(recovery.Revision + 7, ct));
            Check(!(await operation.InspectRecoveryAsync(ct)).CancellationRequested,
                "Stale cancellation revision changed intent.");
            var canceled = await operation.RequestCancellationAsync(recovery.Revision, ct);
            Check(canceled.Requested && canceled.Revision == recovery.Revision + 1,
                "Cancellation was not durably accepted.");
        }
        connection.AssertExited();
        await using (var client = await connection.ConnectAsync(ct))
        {
            var found = await client.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == id, "Cancellation lost operation identity.");
            var operation = client.Operations.GetHandle(id);
            Check((await operation.WaitForCompletionAsync(ct: ct)).State == "canceled", "Process was not canceled.");
            var recovery = await operation.InspectRecoveryAsync(ct);
            Check(recovery.TerminalState == "canceled" && recovery.CancellationRequested, "Cancellation recovery mismatch.");
            var again = await operation.RequestCancellationAsync(recovery.Revision, ct);
            Check(!again.Requested && again.State == "canceled", "Terminal cancellation changed outcome.");
            Check((await client.Workflows.StartAsync(fixture.Request, ct)).OperationId == id,
                "Retry redispatched a canceled operation.");
            fixture.Finish();
            await Task.Delay(200, ct);
            Check(fixture.Effects().SequenceEqual(["started"]), "Canceled external process continued executing.");
        }
        connection.AssertExited();
        Console.WriteLine("SSH cancellation passed: reconnect, revision conflict, durable cancellation and no redispatch.");
    }

    private static async Task LostReplyAsync(TransportTestHost host, CancellationToken ct)
    {
        var fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "lost-reply", ct);
        using var faulty = new SshWorkflowConnection(host.Endpoint, fixture.Workspace, dropStartReply: true);
        await using (var client = await faulty.ConnectAsync(ct))
        {
            await RejectAsync<ConnectionLostException>(() => client.Workflows.StartAsync(fixture.Request, ct));
            Check(File.Exists(faulty.LostReply), "Connection failed before durable acceptance; fault test is invalid.");
            // A broken SDK connection must fail locally, without launching a replacement SSH session.
            await RejectAsync<ConnectionLostException>(() => client.Operations.FindAsync(fixture.Request.ClientRequestId, ct));
        }
        faulty.AssertExited();
        await fixture.WaitForStartAsync(ct);
        using var fresh = new SshWorkflowConnection(host.Endpoint, fixture.Workspace);
        await using (var client = await fresh.ConnectAsync(ct))
        {
            // Recovery only: deliberately no second workflow/start call after the ambiguous outcome.
            var found = await client.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == File.ReadAllText(faulty.LostReply), "Lost response recovery found another operation.");
            var operation = client.Operations.GetHandle(found.OperationId);
            fixture.Finish();
            Check((await operation.WaitForCompletionAsync(ct: ct)).State == "succeeded", "Lost reply interrupted execution.");
            await VerifyReplayAsync(operation, 0, ct);
            Check(fixture.Effects().SequenceEqual(["started", "finished"]), "Ambiguous response caused duplicate effects.");
        }
        fresh.AssertExited();
        Console.WriteLine("SSH lost-reply passed: acceptance response dropped, explicit lookup/reconnect, no automatic redispatch.");
    }

    private static async Task VerifyReplayAsync(WorkflowOperation operation, long cursor, CancellationToken ct)
    {
        var all = await operation.ReadEventsAsync(0, 100, ct);
        Check(!all.HasMore && all.NextSequence > cursor, "Missing final event history.");
        var expected = all.Events.Where(evt => evt.GetProperty("sequence").GetInt64() > cursor)
            .Select(evt => evt.GetRawText()).ToArray();
        var replay = new List<string>();
        while (true)
        {
            var page = await operation.ReadEventsAsync(cursor, 1, ct);
            Check(page.AfterSequence == cursor, "Replay did not honor the cursor.");
            replay.AddRange(page.Events.Select(evt => evt.GetRawText()));
            if (page.Events.Count == 0) break;
            Check(page.NextSequence > cursor, "Replay cursor did not advance.");
            cursor = page.NextSequence;
        }
        Check(replay.SequenceEqual(expected), "Reconnect replay skipped or duplicated events.");
    }
}
