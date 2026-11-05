using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Reporting;

internal static class WorkflowDagTests
{
    private static int _caseCount;

    public static async Task RunAsync()
    {
        await OrderingAndSelection();
        await ValidationPrecedence();
        await ImplicitEdges();
        Console.WriteLine($"Workflow DAG characterization passed ({_caseCount} planner cases).");
    }

    private static async Task OrderingAndSelection()
    {
        (string Name, string[] Nodes, string[] Only, string[] Expected)[] cases =
        [
            ("stable layers", [Inspect("a", "b"), Inspect("b"), Inspect("c")], [], ["b", "c", "a"]),
            ("multiple roots and diamond", [Inspect("a", "b", "c"), Inspect("b", "d"), Inspect("c", "d"), Inspect("d"), Inspect("e")], [], ["d", "e", "b", "c", "a"]),
            ("independent branches", [Inspect("a", "b"), Inspect("b"), Inspect("c", "d"), Inspect("d")], [], ["b", "d", "a", "c"]),
            ("empty selection selects all", [Inspect("a", "b"), Inspect("b")], [], ["b", "a"]),
            ("singleton", [Inspect("only")], [], ["only"]),
            ("single selection closes ancestors", [Inspect("a", "b"), Inspect("b"), Inspect("c")], ["a"], ["b", "a"]),
            ("multiple duplicate selection", [Inspect("a", "b"), Inspect("b"), Inspect("c")], ["a", "a", "c"], ["b", "c", "a"]),
            ("transitive selection closure", [Inspect("a", "b"), Inspect("b", "c"), Inspect("c"), Inspect("other")], ["a"], ["c", "b", "a"]),
            ("case-distinct IDs", [Inspect("Alpha"), Inspect("alpha")], ["Alpha"], ["Alpha"]),
        ];

        foreach ((string? Name, string[]? Nodes, string[]? Only, string[]? Expected) in cases)
        {
            WorkflowPlan plan = await Load(Document(Nodes), Only);
            Equal(Expected, plan.Nodes.Select(n => n.Definition.Id), Name + " order/closure");
            Check(plan.ManifestHash == Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Document(Nodes)))), Name + " manifest hash");
            Check(plan.Nodes.All(n => n.Definition.Kind == "inspect"), Name + " projection kind");
            Check(plan.Nodes.All(n => n.Target.BuildDirectory.EndsWith("missing-build", StringComparison.Ordinal)),
                Name + " target projection");
            Check(plan.Nodes.All(n => n.Diagnostics.Select(d => d.Severity).SequenceEqual(["Observed", "Unverified", "Unverified"])
                                      && n.Diagnostics[0].Message == "Session mode=observe; policy=strict; generation=Unknown; provenance=Unknown."
                                      && n.Diagnostics[1].Message == "Configuration freshness and native output freshness are Unverified; inspection does not run CMake."
                                      && n.Diagnostics[2].Message == "Build directory does not exist. Explicit configure is required."),
                Name + " public plan diagnostic projection");
            if (Name == "stable layers")
            {
                AssertPlanView(plan, Expected);
            }
        }

        await Throws<ArgumentException>([Inspect("self", "self")], [], "Workflow contains a dependency cycle: self", "self cycle");
        await Throws<ArgumentException>([Inspect("cycle-a", "cycle-b"), Inspect("cycle-b", "cycle-a"), Inspect("ok")], [], "Workflow contains a dependency cycle: cycle-a, cycle-b", "multi-node cycle");
        await Throws<ArgumentException>([Inspect("selected"), Inspect("cycle-a", "cycle-b"), Inspect("cycle-b", "cycle-a")], ["selected"], "Workflow contains a dependency cycle: cycle-a, cycle-b", "unselected cycle");
        await Throws<ArgumentException>([Inspect("a", "missing")], [], "a: unknown dependency 'missing'.", "unknown dependency");
        await Throws<ArgumentException>([Inspect("Alpha")], ["alpha"], "Unknown selected node 'alpha'.", "ordinal selected IDs");
        await Throws<ArgumentException>([Inspect("a")], ["missing"], "Unknown selected node 'missing'.", "unknown selection");
        await Throws<ArgumentException>([Inspect("ok")], ["first-missing", "last-missing"],
            "Unknown selected node 'last-missing'.", "last invalid selection wins");
        WorkflowPlan edgeOrder = await Load(Document([Inspect("a", "b", "c", "b"), Inspect("b"), Inspect("c")]), []);
        Equal(["b", "c"], edgeOrder.Nodes.Single(n => n.Definition.Id == "a").Dependencies,
            "dependency deduplication preserves first occurrence");
    }

    private static async Task ValidationPrecedence()
    {
        await Throws<ArgumentException>(
            [Inspect("first", "missing"), /*lang=json,strict*/ "{\"id\":\"later\",\"kind\":\"inspect\",\"context\":\"absent\"}"], [],
            "first: unknown dependency 'missing'.", "dependency before later context error");
        await Throws<ArgumentException>([Inspect("first", "missing")], ["also-missing"],
            "first: unknown dependency 'missing'.", "dependency before unknown selection");
        await Throws<ArgumentException>(
            [Inspect("first", "missing"), /*lang=json,strict*/ "{\"id\":\"later\",\"kind\":\"run\",\"from\":\"no-build\"}"], [],
            "first: unknown dependency 'missing'.", "dependency before later invalid from");
        await Throws<ArgumentException>([Inspect("lower", "Upper"), Inspect("upper")], [],
            "lower: unknown dependency 'Upper'.", "dependency IDs are ordinal");
        await Throws<ArgumentException>([Inspect("a", "b"), Inspect("b", "a")], ["missing"],
            "Workflow contains a dependency cycle: a, b", "cycle before unknown selection");
        await Throws<ArgumentException>([/*lang=json,strict*/ "{\"id\":\"job\",\"kind\":\"run\",\"from\":\"no-build\"}"], [],
            "job: from must refer to a build node.", "invalid from reference");
    }

    private static async Task ImplicitEdges()
    {
        string[] fromNodes =
        [
            /*lang=json,strict*/
                                 "{\"id\":\"run\",\"kind\":\"run\",\"from\":\"build\",\"arguments\":[]}",
            /*lang=json,strict*/
                                 "{\"id\":\"build\",\"kind\":\"build\",\"context\":\"ctx\",\"target\":\"app\"}",
            Inspect("independent")
        ];
        WorkflowPlan plan = await Load(Document(fromNodes), ["run"]);
        Equal(["build", "run"], plan.Nodes.Select(n => n.Definition.Id), "from implicit edge selection");
        Equal(["build"], plan.Nodes[1].Dependencies, "from implicit dependency");
        fromNodes[0] = /*lang=json,strict*/ "{\"id\":\"run\",\"kind\":\"run\",\"from\":\"build\",\"dependsOn\":[\"build\"],\"arguments\":[]}";
        WorkflowPlan deduplicatedFromPlan = await Load(Document(fromNodes), ["run"]);
        Equal(["build"], deduplicatedFromPlan.Nodes[1].Dependencies, "from edge deduplication");

        string[] artifactNodes =
        [
            /*lang=json,strict*/
                                 "{\"id\":\"consumer\",\"kind\":\"inspect\",\"context\":\"ctx\",\"inputs\":[{\"from\":\"producer\",\"artifact\":\"result\"}]}",
            /*lang=json,strict*/
                                 "{\"id\":\"producer\",\"kind\":\"configure\",\"context\":\"ctx\",\"outputs\":[{\"name\":\"result\",\"path\":\"result.txt\",\"root\":\"manifest\"}]}",
            Inspect("independent")
        ];
        WorkflowPlan artifactPlan = await Load(Document(artifactNodes), ["consumer"]);
        Equal(["producer", "consumer"], artifactPlan.Nodes.Select(n => n.Definition.Id), "artifact implicit edge selection");
        Equal(["producer"], artifactPlan.Nodes[1].Dependencies, "artifact implicit dependency");
        artifactNodes[0] = /*lang=json,strict*/ "{\"id\":\"consumer\",\"kind\":\"inspect\",\"context\":\"ctx\",\"dependsOn\":[\"producer\"],\"inputs\":[{\"from\":\"producer\",\"artifact\":\"result\"}]}";
        WorkflowPlan deduplicatedArtifactPlan = await Load(Document(artifactNodes), ["consumer"]);
        Equal(["producer"], deduplicatedArtifactPlan.Nodes[1].Dependencies, "artifact edge deduplication");
    }

    private static string Inspect(string id, params string[] dependencies)
    {
        string deps = string.Join(",", dependencies.Select(d => "\"" + d + "\""));
        return "{\"id\":\"" + id + "\",\"kind\":\"inspect\",\"context\":\"ctx\",\"dependsOn\":[" + deps + "]}";
    }

    private static string Document(IEnumerable<string> nodes) =>
        "{\"schemaVersion\":1,\"contexts\":{\"ctx\":{\"buildDirectory\":\"missing-build\"}},\"nodes\":[" +
        string.Join(",", nodes) + "]}";

    private static async Task<WorkflowPlan> Load(string json, IReadOnlyList<string> only)
    {
        _caseCount++;
        string root = Path.Combine(Path.GetTempPath(), "workflow-dag-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        try
        {
            return await WorkflowPlanner.LoadSnapshotAsync(Path.Combine(root, "manifest.json"),
                Encoding.UTF8.GetBytes(json), only, null, CancellationToken.None);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertPlanView(WorkflowPlan plan, string[] expectedIds)
    {
        Check(plan.ManifestHash == "175D95D3E79689D92F2DC683B2C39D9A4D29FAB43185EA06CCBC60AED888CC75",
            "stable-layer manifest hash");
        WorkflowPlanView view = WorkflowQueries.PlanView(plan);
        Check(view.SchemaVersion == 1 && view.Workflow == plan.File && view.ManifestHash == plan.ManifestHash
              && view.Valid, "public plan view identity/validity");
        Equal(expectedIds, view.Nodes.Select(node => node.Id), "public plan view order");
        Check(view.Nodes.All(node => node.Kind == "inspect" && string.IsNullOrEmpty(node.Target) && node.Configuration is null
                                      && node.BuildDirectory.EndsWith("missing-build", StringComparison.Ordinal)
                                      && node.ObservedState == "Unknown" && node.Resources.Count == 0
                                      && !node.Exclusive && node.Inputs.Count == 0 && node.Outputs.Count == 0
                                      && node.Constraints is null), "public plan view node projection");
        WorkflowPlanNodeView first = view.Nodes[0];
        Check(first.Diagnostics.Select(d => d.Severity).SequenceEqual(["Observed", "Unverified", "Unverified"])
              && first.Diagnostics[0].Message == "Session mode=observe; policy=strict; generation=Unknown; provenance=Unknown."
              && first.Diagnostics[1].Message == "Configuration freshness and native output freshness are Unverified; inspection does not run CMake."
              && first.Diagnostics[2].Message == "Build directory does not exist. Explicit configure is required.",
            "public plan view diagnostics");
        Equal([], view.Nodes[0].Dependencies, "public plan view b dependencies");
        Equal([], view.Nodes[1].Dependencies, "public plan view c dependencies");
        Equal(["b"], view.Nodes[2].Dependencies, "public plan view a dependencies");
    }

    private static async Task Throws<T>(string[] nodes, string[] only, string message, string name)
        where T : Exception
    {
        try
        {
            _ = await Load(Document(nodes), only);
        }
        catch (T error)
        {
            Check(error.GetType() == typeof(T), name + " exception type: " + error.GetType().FullName);
            Check(error.Message == message, name + " diagnostic: " + error.Message);
            return;
        }

        throw new System.Diagnostics.UnreachableException(name + " expected " + typeof(T).Name);
    }

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string name)
    {
        string[] left = [.. expected];
        string[] right = [.. actual];
        Check(left.SequenceEqual(right), name + ": expected [" + string.Join(", ", left) + "] got [" + string.Join(", ", right) + "]");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new System.Diagnostics.UnreachableException(message);
        }
    }
}
