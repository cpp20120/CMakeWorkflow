using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Host.Workflows;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class OperationListTests
{
    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException("Operation list: " + message);
        }
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new System.Diagnostics.UnreachableException("Operation list: invalid cursor or query was accepted");
    }

    public static void Run(string parent)
    {
        string workspace = Directory.CreateDirectory(Path.Combine(parent, "operation-list")).FullName;
        string manifest = Path.Combine(workspace, "input.json");
        byte[] bytes = Encoding.UTF8.GetBytes(/*lang=json,strict*/ "{\"operation\":\"compile\"}");
        File.WriteAllBytes(manifest, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        List<string> ids = [];
        using (DurableAcceptanceStore store = new(workspace))
        {
            for (int i = 0; i < 5; ++i)
            {
                string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"job-{i}")));
                DurableImmutableRequest request = new($"job-{i}", manifest, sha, workspace, digest,
                    i % 2 == 1 ? "process" : "workflow");
                ids.Add(store.AcceptImmutable(request).OperationId);
            }
            _ = store.AuthorizeDispatch(ids[2]);
        }

        string cursor;
        using (DurableAcceptanceStore reader = new(workspace, readOnly: true))
        {
            OperationListPage page = reader.ListOperations(new(Limit: 2));
            Check(page.SchemaVersion == 1 && page.Items.Count == 2 &&
                  page.Items[0].OperationId == ids[4] && page.Items[1].OperationId == ids[3] &&
                  page.Items[1].Kind == "process" && page.NextCursor is not null,
                "first page identity, order or domain tag");
            cursor = page.NextCursor!;
            Reject(() => reader.ListOperations(new(Limit: 0)));
            Reject(() => reader.ListOperations(new(Cursor: "garbage")));
            Reject(() => reader.ListOperations(new(Limit: 2, Cursor: cursor, State: "succeeded")));
            Reject(() => reader.ListOperations(new(Domain: "process' OR 1=1")));
            OperationListPage process = reader.ListOperations(new(Domain: "process"));
            Check(process.Items.Count == 2 && process.Items[0].OperationId == ids[3] &&
                  process.Items[1].OperationId == ids[1] && process.NextCursor is null,
                "domain filtering");
            OperationListPage authorized = reader.ListOperations(new(State: "dispatch_authorized"));
            Check(authorized.Items.Count == 1 && authorized.Items[0].OperationId == ids[2],
                "effective-state filtering");
        }

        // A concurrent admission must not slip into the remainder of an existing cursor traversal.
        using (DurableAcceptanceStore writer = new(workspace))
        {
            string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("newest")));
            _ = writer.AcceptImmutable(new("newest", manifest, sha, workspace, digest, "process"));
        }
        string database = Path.Combine(workspace, ".cmakeworkflow", "operations.sqlite");
        byte[] before = SHA256.HashData(File.ReadAllBytes(database));
        using (DurableAcceptanceStore reader = new(workspace, readOnly: true))
        {
            OperationListPage second = reader.ListOperations(new(Limit: 2, Cursor: cursor));
            Check(second.Items.Count == 2 && second.Items[0].OperationId == ids[2] &&
                  second.Items[1].OperationId == ids[1] && second.NextCursor is not null,
                "second page after concurrent admission");
            OperationListPage third = reader.ListOperations(new(Limit: 2, Cursor: second.NextCursor));
            Check(third.Items.Count == 1 && third.Items[0].OperationId == ids[0] &&
                  third.NextCursor is null, "last page");
            Check(reader.ListOperations(new(Limit: 2)).Items[0].ClientRequestId == "newest",
                "new cursor did not observe latest admission");
        }
        Check(SHA256.HashData(File.ReadAllBytes(database)).SequenceEqual(before),
            "read-only list modified SQLite bytes");
        string foreign = Directory.CreateDirectory(Path.Combine(parent, "foreign-list")).FullName;
        using (DurableAcceptanceStore store = new(foreign))
        {
            Reject(() => store.ListOperations(new(Cursor: cursor)));
        }

        Console.WriteLine("Operation list checks passed: domain/state filters, cursor isolation, stable admission window, read-only restart.");
    }
}