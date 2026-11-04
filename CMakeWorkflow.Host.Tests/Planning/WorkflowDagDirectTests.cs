using CMakeWorkflow;

internal static class WorkflowDagDirectTests
{
    public static void Run()
    {
        OrderingAndClosure();
        ValidationAndFailures();
        Console.WriteLine("Workflow DAG direct checks passed.");
    }

    private static void OrderingAndClosure()
    {
        var layers = Select([("a", "b"), ("b", ""), ("c", "")], []);
        Equal(["b", "c", "a"], layers.OrderedIds, "stable layer order");
        SetEqual(["a", "b", "c"], layers.SelectedIds, "empty selection selects all");

        var diamond = Select([("a", "b,c"), ("b", "d"), ("c", "d"), ("d", ""), ("e", "")], []);
        Equal(["d", "e", "b", "c", "a"], diamond.OrderedIds, "diamond and ready-root order");

        var branches = Select([("left", "left-root"), ("left-root", ""), ("right", "right-root"),
            ("right-root", "")], []);
        Equal(["left-root", "right-root", "left", "right"], branches.OrderedIds,
            "independent branches");

        var selected = Select([("a", "b"), ("b", ""), ("c", "")], ["a"]);
        Equal(["b", "c", "a"], selected.OrderedIds, "full ordering does not depend on selection");
        SetEqual(["a", "b"], selected.SelectedIds, "single transitive closure");

        var repeated = Select([("a", "b"), ("b", ""), ("c", "")], ["a", "a", "c"]);
        SetEqual(["a", "b", "c"], repeated.SelectedIds, "multiple duplicate selection");

        var deep = Select([("a", "b"), ("b", "c"), ("c", ""), ("unrelated", "")], ["a"]);
        SetEqual(["a", "b", "c"], deep.SelectedIds, "multi-level transitive closure");

        var caseDistinct = Select([("Alpha", ""), ("alpha", "")], ["Alpha"]);
        SetEqual(["Alpha"], caseDistinct.SelectedIds, "ordinal selection IDs");
        Equal(["Alpha", "alpha"], caseDistinct.OrderedIds, "case-distinct node IDs");

        var singleton = Select([("solo", "")], ["solo"]);
        Equal(["solo"], singleton.OrderedIds, "singleton order");
        SetEqual(["solo"], singleton.SelectedIds, "singleton selection");

        var opaqueIds = Select([("opaque node", "dependency / worker"), ("dependency / worker", "")],
            ["opaque node"]);
        Equal(["dependency / worker", "opaque node"], opaqueIds.OrderedIds,
            "graph IDs have no domain identifier validation");
    }

    private static void ValidationAndFailures()
    {
        WorkflowDag.ValidateDependencies("consumer", ["known"],
            new HashSet<string>(["known"], StringComparer.Ordinal));
        Throws<ArgumentException>(
            () => WorkflowDag.ValidateDependencies("consumer", ["missing", "also-missing"],
                new HashSet<string>(StringComparer.Ordinal)),
            "consumer: unknown dependency 'missing'.", "first unknown dependency wins");
        Throws<ArgumentException>(
            () => WorkflowDag.ValidateDependencies("consumer", ["Known"],
                new HashSet<string>(["known"], StringComparer.Ordinal)),
            "consumer: unknown dependency 'Known'.", "dependency validation is ordinal");

        Throws<ArgumentException>(() => Select([("self", "self")], []),
            "Workflow contains a dependency cycle: self", "self-cycle diagnostic");
        Throws<ArgumentException>(() => Select([("a", "b"), ("b", "a"), ("blocked", "a"), ("free", "")], ["free"]),
            "Workflow contains a dependency cycle: a, b, blocked", "cycle includes downstream blocked nodes");
        Throws<ArgumentException>(() => Select([("selected", ""), ("a", "b"), ("b", "a")], ["selected"]),
            "Workflow contains a dependency cycle: a, b", "unselected cycle precedes selection");
        Throws<ArgumentException>(() => Select([("a", "b"), ("b", "a")], ["missing"]),
            "Workflow contains a dependency cycle: a, b", "cycle precedes unknown selection");
        Throws<ArgumentException>(() => Select([("a", "")], ["missing"]),
            "Unknown selected node 'missing'.", "unknown selection diagnostic");
        Throws<ArgumentException>(() => Select([("Alpha", "")], ["alpha"]),
            "Unknown selected node 'alpha'.", "selection is ordinal");
        Throws<ArgumentException>(() => Select([("a", "")], ["first-missing", "last-missing"]),
            "Unknown selected node 'last-missing'.", "stack selection error precedence");
    }

    private static (List<string> OrderedIds, HashSet<string> SelectedIds) Select(
        (string Id, string Dependencies)[] entries, IReadOnlyList<string> only)
    {
        var ids = entries.Select(entry => entry.Id).ToArray();
        var dependencies = entries.ToDictionary(entry => entry.Id,
            entry => entry.Dependencies.Length == 0
                ? Array.Empty<string>()
                : entry.Dependencies.Split(',', StringSplitOptions.RemoveEmptyEntries),
            StringComparer.Ordinal);
        return WorkflowDag.Select(ids, dependencies, only);
    }

    private static void Throws<T>(Action action, string message, string name) where T : Exception
    {
        try
        {
            action();
        }
        catch (T error)
        {
            Check(error.GetType() == typeof(T), name + " exception type: " + error.GetType().FullName);
            Check(error.Message == message, name + " diagnostic: " + error.Message);
            return;
        }

        throw new Exception(name + " expected " + typeof(T).Name);
    }

    private static void Equal(IEnumerable<string> expected, IEnumerable<string> actual, string name)
    {
        var left = expected.ToArray();
        var right = actual.ToArray();
        Check(left.SequenceEqual(right), name + ": expected [" + string.Join(", ", left) + "] got [" +
              string.Join(", ", right) + "]");
    }

    private static void SetEqual(IEnumerable<string> expected, IReadOnlySet<string> actual, string name)
    {
        var expectedSet = new HashSet<string>(expected, StringComparer.Ordinal);
        Check(expectedSet.SetEquals(actual), name + ": set did not match expected values");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
