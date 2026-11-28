using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ExecutionReport;

namespace CMakeWorkflow.CMake.Reporting;

internal sealed record NativeExecutionCheckpoint(string WorkspaceId, string OperationId, string RequestDigest,
    string WorkflowDocumentId, string PlanDocumentId, string DispatchIdentity, string RunDirectory,
    long Generation, long EventCursor, WorkflowReport Report);

internal static partial class NativeExecutionReportCodec
{
    internal static byte[] EncodeCheckpoint(NativeExecutionCheckpoint value)
    {
        MessageBuilder message = new(Limits);
        Wire.ExecutionCheckpoint.Builder root = Wire.ExecutionCheckpoint.InitRoot(message);
        root.SetVersion(1);
        root.SetWorkspaceId(value.WorkspaceId);
        root.SetOperationId(value.OperationId);
        root.SetRequestDigest(value.RequestDigest);
        root.SetWorkflowDocumentId(value.WorkflowDocumentId);
        root.SetPlanDocumentId(value.PlanDocumentId);
        root.SetDispatchIdentity(value.DispatchIdentity);
        root.SetRunDirectory(value.RunDirectory);
        root.SetGeneration(value.Generation);
        root.SetEventCursor(value.EventCursor);
        Write(root.InitReport(), value.Report);
        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static NativeExecutionCheckpoint DecodeCheckpoint(ReadOnlySpan<byte> frame)
    {
        Wire.ExecutionCheckpoint.Reader root = Wire.ExecutionCheckpoint.ReadRoot(FrameCodec.Read(frame, Limits));
        if (root.Version != 1 || !root.HasReport || root.Generation <= 0 || root.EventCursor < 0)
        {
            throw new InvalidDataException("Invalid native runner checkpoint.");
        }

        WorkflowReport report = ReadWorkflowReport(root.Report);
        return report.SchemaVersion != 1 || report.ResumeCount < 0 ||
               report.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != report.Nodes.Count
            ? throw new InvalidDataException("Invalid native runner checkpoint model.")
            : new(Required(root.DecodeWorkspaceId()), Required(root.DecodeOperationId()),
                Required(root.DecodeRequestDigest()), Required(root.DecodeWorkflowDocumentId()), Required(root.DecodePlanDocumentId()),
                Required(root.DecodeDispatchIdentity()), Required(root.DecodeRunDirectory()), root.Generation, root.EventCursor, report);
    }

    internal static NodeResult CloneNode(NodeResult value)
    {
        MessageBuilder message = new(Limits);
        Write(Wire.NodeResult.InitRoot(message), value);
        return ReadNodeResult(Wire.NodeResult.ReadRoot(FrameCodec.Read(CapnpWire.Frame(message.CopySegment()), Limits)));
    }
}
