// Prevent the storage-free Client read projections from silently diverging from
// existing History Host DTOs. No dependency from Client to the SQLite assembly.
internal static class HistoryWireModelParityTests
{
    private static string Shape(Type type) => type.IsGenericType
        ? type.GetGenericTypeDefinition().Name + "<" +
          string.Join(",", type.GetGenericArguments().Select(Shape)) + ">"
        : type.Name;

    private static void Check(Type host, Type client)
    {
        var expected = host.GetProperties().Select(p => (p.Name, Type: Shape(p.PropertyType)))
            .OrderBy(p => p.Name).ToArray();
        var actual = client.GetProperties().Select(p => (p.Name, Type: Shape(p.PropertyType)))
            .OrderBy(p => p.Name).ToArray();
        if (!expected.SequenceEqual(actual))
            throw new Exception($"History wire contract drift: {host.Name} / {client.Name}");
    }

    public static void Run()
    {
        Check(typeof(CMakeWorkflow.History.HistoryRun), typeof(CMakeWorkflow.Client.HistoryRun));
        Check(typeof(CMakeWorkflow.History.HistoryRunPage), typeof(CMakeWorkflow.Client.HistoryRunPage));
        Check(typeof(CMakeWorkflow.Host.OperationNodeView), typeof(CMakeWorkflow.Client.OperationNodeView));
        Check(typeof(CMakeWorkflow.History.HistoryDetail), typeof(CMakeWorkflow.Client.HistoryDetail));
        Check(typeof(CMakeWorkflow.History.HistoryNode), typeof(CMakeWorkflow.Client.HistoryNode));
        Check(typeof(CMakeWorkflow.History.HistoryMeasurement), typeof(CMakeWorkflow.Client.HistoryMeasurement));
        Check(typeof(CMakeWorkflow.History.HistoryArtifact), typeof(CMakeWorkflow.Client.HistoryArtifact));
        Check(typeof(CMakeWorkflow.History.HistoryHostEnvironment), typeof(CMakeWorkflow.Client.HistoryHostEnvironment));
        Check(typeof(CMakeWorkflow.History.HistoryBuildEnvironment), typeof(CMakeWorkflow.Client.HistoryBuildEnvironment));
        Check(typeof(CMakeWorkflow.History.HistoryStats), typeof(CMakeWorkflow.Client.HistoryStats));
        Check(typeof(CMakeWorkflow.History.HistoryDifference), typeof(CMakeWorkflow.Client.HistoryDifference));
        Check(typeof(CMakeWorkflow.History.HistoryDetection), typeof(CMakeWorkflow.Client.HistoryDetection));
        Check(typeof(CMakeWorkflow.History.HistoryDetectionReport), typeof(CMakeWorkflow.Client.HistoryDetectionReport));
        Console.WriteLine("History SDK/Host read model parity passed.");
    }
}
