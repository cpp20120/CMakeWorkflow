namespace CMakeWorkflow;

internal static class WorkflowDag
{
    internal static void ValidateDependencies(string nodeId, IEnumerable<string> dependencies,
        IReadOnlySet<string> nodeIds)
    {
        foreach (var dependency in dependencies)
            if (!nodeIds.Contains(dependency))
                throw new ArgumentException($"{nodeId}: unknown dependency '{dependency}'.");
    }

    internal static (List<string> OrderedIds, HashSet<string> SelectedIds) Select(
        IReadOnlyList<string> orderedNodeIds,
        IReadOnlyDictionary<string, string[]> dependencies,
        IReadOnlyList<string> only)
    {
        // Validate the whole graph, including unselected branches, before selection.
        var orderedIds = new List<string>();
        var remaining = orderedNodeIds.ToList();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            var ready = remaining.Where(id => dependencies[id].All(visited.Contains)).ToArray();
            if (ready.Length == 0)
                throw new ArgumentException(
                    $"Workflow contains a dependency cycle: {string.Join(", ", remaining)}");

            foreach (var id in ready)
            {
                orderedIds.Add(id);
                visited.Add(id);
                remaining.Remove(id);
            }
        }

        var knownIds = new HashSet<string>(orderedNodeIds, StringComparer.Ordinal);
        var selectedIds = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(only.Count == 0 ? orderedNodeIds : only);
        while (pending.TryPop(out var id))
        {
            if (!knownIds.Contains(id)) throw new ArgumentException($"Unknown selected node '{id}'.");
            if (!selectedIds.Add(id)) continue;
            foreach (var dependency in dependencies[id]) pending.Push(dependency);
        }

        return (orderedIds, selectedIds);
    }
}
