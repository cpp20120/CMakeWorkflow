using CMakeWorkflow.Client.Workflows;
using StreamJsonRpc;
using static CMakeWorkflow.Host.Tests.Transport.SshTransportTests;
using CMakeWorkflowClient = CMakeWorkflow.Client.CMakeWorkflowClient;

namespace CMakeWorkflow.Host.Tests.Transport;

internal static class SshWorkflowTests
{
    private static void Check(bool condition, string message) => SshTransportTests.Check(condition, message);

    internal static async Task RunAsync(string? scenario)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Remote workflow fixture requires Linux/POSIX, CMake and Ninja.");
        }

        if (scenario is not (null or "execution" or "cancellation" or "paths" or "lost-reply"))
        {
            throw new ArgumentException("Expected execution, cancellation, paths or lost-reply.");
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(70));
        CancellationToken ct = deadline.Token;
        await using TransportTestHost host = new();
        await host.ReadyAsync(ct);
        if (scenario is null or "execution")
        {
            await ExecuteAsync(host, ct);
        }

        if (scenario is null or "cancellation")
        {
            await CancelAsync(host, ct);
        }

        if (scenario is null or "paths")
        {
            await SshWorkflowPathTests.RunAsync(host, ct);
        }

        if (scenario is null or "lost-reply")
        {
            await LostReplyAsync(host, ct);
        }

        Console.WriteLine("SSH durable workflow tests passed: " + (scenario ?? "execution, cancellation, paths, lost-reply") + ".");
    }

    private static async Task ExecuteAsync(TransportTestHost host, CancellationToken ct)
    {
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "execution", ct);
        using SshWorkflowConnection first = new(host.Endpoint, fixture.Workspace);
        using SshWorkflowConnection second = new(host.Endpoint, fixture.Workspace);
        string id;
        long cursor;
        await using (CMakeWorkflowClient client = await first.ConnectAsync(ct))
        {
            WorkflowOperation operation = await client.Workflows.StartAsync(fixture.Request, ct);
            id = operation.OperationId;
            await fixture.WaitForStartAsync(ct);
            _ = await RejectAsync<RemoteInvocationException>(() => operation.GetReportAsync(ct));
            EventPage page = await operation.ReadEventsAsync(0, 1, ct);
            Check(page.Events.Count == 1 && page.NextSequence > 0, "No live event cursor.");
            cursor = page.NextSequence;
            await using CMakeWorkflowClient other = await second.ConnectAsync(ct);
            WorkflowOperation retry = await other.Workflows.StartAsync(fixture.Request, ct);
            Check(retry.OperationId == id, "Concurrent client duplicated durable acceptance.");
            _ = await RejectAsync<RemoteInvocationException>(() => other.Workflows.StartAsync(
                fixture.Request with { Jobs = 2 }, ct));
        }
        first.AssertExited();
        second.AssertExited();
        Check(fixture.Effects().SequenceEqual(["started"]), "Disconnect canceled or completed the gated effect.");

        // An explicitly identical retry must retain the accepted immutable snapshot.
        await File.WriteAllTextAsync(fixture.Request.Manifest, "invalid replacement after acceptance", CancellationToken.None);
        await using (CMakeWorkflowClient reconnected = await second.ConnectAsync(ct))
        {
            Check(reconnected.Host.ProcessId == Environment.ProcessId, "Reconnection activated another Host.");
            OperationSnapshot found = await reconnected.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == id && found.ManifestSha256 == fixture.Request.ManifestSha256 &&
                  found.ManifestBaseDirectory == fixture.Workspace, "Recovered acceptance identity changed.");
            WorkflowOperation operation = reconnected.Operations.GetHandle(found.OperationId);
            fixture.Finish();
            OperationSnapshot final = await operation.WaitForCompletionAsync(ct: ct);
            Check(final.State == "succeeded", "Remote workflow failed: " + final.State);
            OperationReportSnapshot report = await operation.GetReportAsync(ct);
            Check(report.SchemaVersion == 1 && report.Revision.Length == 64 &&
                  report.Report.GetProperty("status").GetString() == "succeeded" &&
                  report.RunDirectory == fixture.Request.ResultsDirectory,
                "Operation-bound terminal report is incorrect.");
            RecoverySnapshot recovery = await operation.InspectRecoveryAsync(ct);
            Check(recovery.TerminalState == "succeeded" && !recovery.CancellationRequested,
                "Disconnect changed durable recovery state.");
            await VerifyReplayAsync(operation, cursor, ct);
            WorkflowOperation retry = await reconnected.Workflows.StartAsync(fixture.Request, ct);
            Check(retry.OperationId == id, "Terminal retry created a new operation.");
            Check(fixture.Effects().SequenceEqual(["started", "finished"]), "Remote effect ran more than once.");
            // A post-finalization edit must not replace the report bound to this operation.
            await File.AppendAllTextAsync(Path.Combine(fixture.Request.ResultsDirectory, "run.json"), " ", CancellationToken.None);
            _ = await RejectAsync<RemoteInvocationException>(() => operation.GetReportAsync(ct));
        }
        second.AssertExited();
        Console.WriteLine("SSH execution passed: two clients, disconnect/reconnect, request identity, immutable snapshot, cursor replay and one effect.");
    }

    private static async Task CancelAsync(TransportTestHost host, CancellationToken ct)
    {
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "cancellation", ct);
        using SshWorkflowConnection connection = new(host.Endpoint, fixture.Workspace);
        string id;
        await using (CMakeWorkflowClient client = await connection.ConnectAsync(ct))
        {
            id = (await client.Workflows.StartAsync(fixture.Request, ct)).OperationId;
            await fixture.WaitForStartAsync(ct);
        }
        connection.AssertExited();
        await using (CMakeWorkflowClient client = await connection.ConnectAsync(ct))
        {
            WorkflowOperation operation = client.Operations.GetHandle(id);
            RecoverySnapshot recovery = await operation.InspectRecoveryAsync(ct);
            _ = await RejectAsync<RemoteInvocationException>(() => operation.RequestCancellationAsync(recovery.Revision + 7, ct));
            Check(!(await operation.InspectRecoveryAsync(ct)).CancellationRequested,
                "Stale cancellation revision changed intent.");
            CancellationReceipt canceled = await operation.RequestCancellationAsync(recovery.Revision, ct);
            Check(canceled.Requested && canceled.Revision == recovery.Revision + 1,
                "Cancellation was not durably accepted.");
        }
        connection.AssertExited();
        await using (CMakeWorkflowClient client = await connection.ConnectAsync(ct))
        {
            OperationSnapshot found = await client.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == id, "Cancellation lost operation identity.");
            WorkflowOperation operation = client.Operations.GetHandle(id);
            Check((await operation.WaitForCompletionAsync(ct: ct)).State == "canceled", "Process was not canceled.");
            RecoverySnapshot recovery = await operation.InspectRecoveryAsync(ct);
            Check(recovery.TerminalState == "canceled" && recovery.CancellationRequested, "Cancellation recovery mismatch.");
            CancellationReceipt again = await operation.RequestCancellationAsync(recovery.Revision, ct);
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
        RemoteWorkflowFixture fixture = await RemoteWorkflowFixture.CreateAsync(host.Root, "lost-reply", ct);
        using SshWorkflowConnection faulty = new(host.Endpoint, fixture.Workspace, dropStartReply: true);
        await using (CMakeWorkflowClient client = await faulty.ConnectAsync(ct))
        {
            _ = await RejectAsync<ConnectionLostException>(() => client.Workflows.StartAsync(fixture.Request, ct));
            Check(File.Exists(faulty.LostReply), "Connection failed before durable acceptance; fault test is invalid.");
            // A broken SDK connection must fail locally, without launching a replacement SSH session.
            _ = await RejectAsync<ConnectionLostException>(() => client.Operations.FindAsync(fixture.Request.ClientRequestId, ct));
        }
        faulty.AssertExited();
        await fixture.WaitForStartAsync(ct);
        using SshWorkflowConnection fresh = new(host.Endpoint, fixture.Workspace);
        await using (CMakeWorkflowClient client = await fresh.ConnectAsync(ct))
        {
            // Recovery only: deliberately no second workflow/start call after the ambiguous outcome.
            OperationSnapshot found = await client.Operations.FindAsync(fixture.Request.ClientRequestId, ct);
            Check(found.OperationId == (await File.ReadAllTextAsync(faulty.LostReply, ct)), "Lost response recovery found another operation.");
            WorkflowOperation operation = client.Operations.GetHandle(found.OperationId);
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
        EventPage all = await operation.ReadEventsAsync(0, 100, ct);
        Check(!all.HasMore && all.NextSequence > cursor, "Missing final event history.");
        string[] expected = [.. all.Events.Where(evt => evt.GetProperty("sequence").GetInt64() > cursor).Select(evt => evt.GetRawText())];
        List<string> replay = [];
        while (true)
        {
            EventPage page = await operation.ReadEventsAsync(cursor, 1, ct);
            Check(page.AfterSequence == cursor, "Replay did not honor the cursor.");
            replay.AddRange(page.Events.Select(evt => evt.GetRawText()));
            if (page.Events.Count == 0)
            {
                break;
            }

            Check(page.NextSequence > cursor, "Replay cursor did not advance.");
            cursor = page.NextSequence;
        }
        Check(replay.SequenceEqual(expected), "Reconnect replay skipped or duplicated events.");
    }
}