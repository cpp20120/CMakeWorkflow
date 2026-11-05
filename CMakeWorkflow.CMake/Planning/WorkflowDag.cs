namespace CMakeWorkflow.CMake.Planning;

internal static class WorkflowDag
{
    internal static void ValidateDependencies(string nodeId, IEnumerable<string> dependencies,
        IReadOnlySet<string> nodeIds)
    {
        foreach (string dependency in dependencies)
        {
            if (!nodeIds.Contains(dependency))
            {
                throw new ArgumentException($"{nodeId}: unknown dependency '{dependency}'.");
            }
        }
    }

    internal static (List<string> OrderedIds, HashSet<string> SelectedIds) Select(
        IReadOnlyList<string> orderedNodeIds,
        IReadOnlyDictionary<string, string[]> dependencies,
        IReadOnlyList<string> only)
    {
        // Validate the whole graph, including unselected branches, before selection.
        List<string> orderedIds = [];
        List<string> remaining = [.. orderedNodeIds];
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (remaining.Count > 0)
        {
            string[] ready = [.. remaining.Where(id => dependencies[id].All(visited.Contains))];
            if (ready.Length == 0)
            {
                throw new ArgumentException(
                    $"Workflow contains a dependency cycle: {string.Join(", ", remaining)}");
            }

            foreach (string? id in ready)
            {
                orderedIds.Add(id);
                _ = visited.Add(id);
                _ = remaining.Remove(id);
            }
        }

        HashSet<string> knownIds = new(orderedNodeIds, StringComparer.Ordinal);
        HashSet<string> selectedIds = new(StringComparer.Ordinal);
        Stack<string> pending = new(only.Count == 0 ? orderedNodeIds : only);
        while (pending.TryPop(out string? id))
        {
            if (!knownIds.Contains(id))
            {
                throw new ArgumentException($"Unknown selected node '{id}'.");
            }

            if (!selectedIds.Add(id))
            {
                continue;
            }

            foreach (string dependency in dependencies[id])
            {
                pending.Push(dependency);
            }
        }

        return (orderedIds, selectedIds);
    }
}
