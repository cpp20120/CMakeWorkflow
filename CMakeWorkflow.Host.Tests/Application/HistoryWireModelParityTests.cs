// Prevent the storage-free Client read projections from silently diverging from
// existing History Host DTOs. No dependency from Client to the SQLite assembly.
using CMakeWorkflow.Client.History;

internal static class HistoryWireModelParityTests
{
    private static string Shape(Type type) => type.IsGenericType
        ? type.GetGenericTypeDefinition().Name + "<" +
          string.Join(",", type.GetGenericArguments().Select(Shape)) + ">"
        : type.Name;

    private static void Check(Type host, Type client)
    {
        (string Name, string Type)[] expected = [.. host.GetProperties().Select(p => (p.Name, Type: Shape(p.PropertyType))).OrderBy(p => p.Name)];
        (string Name, string Type)[] actual = [.. client.GetProperties().Select(p => (p.Name, Type: Shape(p.PropertyType))).OrderBy(p => p.Name)];
        if (!expected.SequenceEqual(actual))
        {
            throw new System.Diagnostics.UnreachableException($"History wire contract drift: {host.Name} / {client.Name}");
        }
    }

    public static void Run()
    {
        Check(typeof(CMakeWorkflow.History.HistoryRun), typeof(HistoryRun));
        Check(typeof(CMakeWorkflow.History.HistoryRunPage), typeof(HistoryRunPage));
        Check(typeof(CMakeWorkflow.Host.OperationNodeView), typeof(CMakeWorkflow.Client.OperationNodeView));
        Check(typeof(CMakeWorkflow.History.HistoryDetail), typeof(HistoryDetail));
        Check(typeof(CMakeWorkflow.History.HistoryNode), typeof(HistoryNode));
        Check(typeof(CMakeWorkflow.History.HistoryMeasurement), typeof(HistoryMeasurement));
        Check(typeof(CMakeWorkflow.History.HistoryArtifact), typeof(HistoryArtifact));
        Check(typeof(CMakeWorkflow.History.HistoryHostEnvironment), typeof(HistoryHostEnvironment));
        Check(typeof(CMakeWorkflow.History.HistoryBuildEnvironment), typeof(HistoryBuildEnvironment));
        Check(typeof(CMakeWorkflow.History.HistoryStats), typeof(HistoryStats));
        Check(typeof(CMakeWorkflow.History.HistoryDifference), typeof(HistoryDifference));
        Check(typeof(CMakeWorkflow.History.HistoryDetection), typeof(HistoryDetection));
        Check(typeof(CMakeWorkflow.History.HistoryDetectionReport), typeof(HistoryDetectionReport));
        Console.WriteLine("History SDK/Host read model parity passed.");
    }
}
