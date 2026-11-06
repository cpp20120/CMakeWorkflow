// Prevent the storage-free Client read projections from silently diverging from
// existing History Host DTOs. No dependency from Client to the SQLite assembly.

using CMakeWorkflow.Client.History;
using HistoryArtifact = CMakeWorkflow.History.Models.HistoryArtifact;
using HistoryBuildEnvironment = CMakeWorkflow.History.Models.HistoryBuildEnvironment;
using HistoryDetail = CMakeWorkflow.History.Models.HistoryDetail;
using HistoryDetection = CMakeWorkflow.History.Analysis.HistoryDetection;
using HistoryDetectionReport = CMakeWorkflow.History.Analysis.HistoryDetectionReport;
using HistoryDifference = CMakeWorkflow.History.Analysis.HistoryDifference;
using HistoryHostEnvironment = CMakeWorkflow.History.Models.HistoryHostEnvironment;
using HistoryMeasurement = CMakeWorkflow.History.Models.HistoryMeasurement;
using HistoryNode = CMakeWorkflow.History.Models.HistoryNode;
using HistoryRun = CMakeWorkflow.History.Models.HistoryRun;
using HistoryStats = CMakeWorkflow.History.Analysis.HistoryStats;
using OperationNodeView = CMakeWorkflow.Host.Workflows.OperationNodeView;

namespace CMakeWorkflow.Host.Tests.Application;

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
        Check(typeof(HistoryRun), typeof(Client.History.HistoryRun));
        Check(typeof(CMakeWorkflow.History.HistoryRunPage), typeof(HistoryRunPage));
        Check(typeof(OperationNodeView), typeof(Client.Workflows.OperationNodeView));
        Check(typeof(HistoryDetail), typeof(Client.History.HistoryDetail));
        Check(typeof(HistoryNode), typeof(Client.History.HistoryNode));
        Check(typeof(HistoryMeasurement), typeof(Client.History.HistoryMeasurement));
        Check(typeof(HistoryArtifact), typeof(Client.History.HistoryArtifact));
        Check(typeof(HistoryHostEnvironment), typeof(Client.History.HistoryHostEnvironment));
        Check(typeof(HistoryBuildEnvironment), typeof(Client.History.HistoryBuildEnvironment));
        Check(typeof(HistoryStats), typeof(Client.History.HistoryStats));
        Check(typeof(HistoryDifference), typeof(Client.History.HistoryDifference));
        Check(typeof(HistoryDetection), typeof(Client.History.HistoryDetection));
        Check(typeof(HistoryDetectionReport), typeof(Client.History.HistoryDetectionReport));
        Console.WriteLine("History SDK/Host read model parity passed.");
    }
}