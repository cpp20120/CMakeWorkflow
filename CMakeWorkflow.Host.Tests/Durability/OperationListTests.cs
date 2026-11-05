using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host;

internal static class OperationListTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception("Operation list: " + message);
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new Exception("Operation list: invalid cursor or query was accepted");
    }

    public static void Run(string parent)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(parent, "operation-list")).FullName;
        var manifest = Path.Combine(workspace, "input.json");
        var bytes = Encoding.UTF8.GetBytes("{\"operation\":\"compile\"}");
        File.WriteAllBytes(manifest, bytes);
        var sha = Convert.ToHexString(SHA256.HashData(bytes));
        var ids = new List<string>();
        using (var store = new DurableAcceptanceStore(workspace))
        {
            for (var i = 0; i < 5; ++i)
            {
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"job-{i}")));
                var request = new DurableImmutableRequest($"job-{i}", manifest, sha, workspace, digest,
                    i % 2 == 1 ? "process" : "workflow");
                ids.Add(store.AcceptImmutable(request).OperationId);
            }
            store.AuthorizeDispatch(ids[2]);
        }

        string cursor;
        using (var reader = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            var page = reader.ListOperations(new(Limit: 2));
            Check(page.SchemaVersion == 1 && page.Items.Count == 2 &&
                  page.Items[0].OperationId == ids[4] && page.Items[1].OperationId == ids[3] &&
                  page.Items[1].Kind == "process" && page.NextCursor is not null,
                "first page identity, order or domain tag");
            cursor = page.NextCursor!;
            Reject(() => reader.ListOperations(new(Limit: 0)));
            Reject(() => reader.ListOperations(new(Cursor: "garbage")));
            Reject(() => reader.ListOperations(new(Limit: 2, Cursor: cursor, State: "succeeded")));
            Reject(() => reader.ListOperations(new(Domain: "process' OR 1=1")));
            var process = reader.ListOperations(new(Domain: "process"));
            Check(process.Items.Count == 2 && process.Items[0].OperationId == ids[3] &&
                  process.Items[1].OperationId == ids[1] && process.NextCursor is null,
                "domain filtering");
            var authorized = reader.ListOperations(new(State: "dispatch_authorized"));
            Check(authorized.Items.Count == 1 && authorized.Items[0].OperationId == ids[2],
                "effective-state filtering");
        }

        // A concurrent admission must not slip into the remainder of an existing cursor traversal.
        using (var writer = new DurableAcceptanceStore(workspace))
        {
            var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("newest")));
            writer.AcceptImmutable(new("newest", manifest, sha, workspace, digest, "process"));
        }
        var database = Path.Combine(workspace, ".cmakeworkflow", "operations.sqlite");
        var before = SHA256.HashData(File.ReadAllBytes(database));
        using (var reader = new DurableAcceptanceStore(workspace, readOnly: true))
        {
            var second = reader.ListOperations(new(Limit: 2, Cursor: cursor));
            Check(second.Items.Count == 2 && second.Items[0].OperationId == ids[2] &&
                  second.Items[1].OperationId == ids[1] && second.NextCursor is not null,
                "second page after concurrent admission");
            var third = reader.ListOperations(new(Limit: 2, Cursor: second.NextCursor));
            Check(third.Items.Count == 1 && third.Items[0].OperationId == ids[0] &&
                  third.NextCursor is null, "last page");
            Check(reader.ListOperations(new(Limit: 2)).Items[0].ClientRequestId == "newest",
                "new cursor did not observe latest admission");
        }
        Check(SHA256.HashData(File.ReadAllBytes(database)).SequenceEqual(before),
            "read-only list modified SQLite bytes");
        var foreign = Directory.CreateDirectory(Path.Combine(parent, "foreign-list")).FullName;
        using (var store = new DurableAcceptanceStore(foreign))
            Reject(() => store.ListOperations(new(Cursor: cursor)));
        Console.WriteLine("Operation list checks passed: domain/state filters, cursor isolation, stable admission window, read-only restart.");
    }
}
