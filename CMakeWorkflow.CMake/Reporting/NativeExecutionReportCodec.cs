using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ExecutionReport;

namespace CMakeWorkflow.CMake.Reporting;

/// <summary>Terminal runner evidence bound to the immutable admission and authorized plan.</summary>
public sealed record NativeExecutionReport(string WorkspaceId, string OperationId, string RequestDigest,
    string WorkflowDocumentId, string PlanDocumentId, string DispatchIdentity, string RunDirectory, int ExitCode,
    WorkflowReport Report, DateTimeOffset? OperatorCancellationRequestedAt = null);

internal static partial class NativeExecutionReportCodec
{
    private static readonly MessageLimits Limits = MessageLimits.Default with
    {
        MaxMessageBytes = 16 * 1024 * 1024,
        MaxSegmentBytes = 16 * 1024 * 1024,
        MaxSegments = 1,
        MaxListElements = 16_384,
        MaxTextBytes = 1024 * 1024,
        MaxNesting = 32,
        MaxTraversalWords = 4 * 1024 * 1024
    };

    internal static byte[] Encode(NativeExecutionReport value)
    {
        Validate(value);
        MessageBuilder message = new(Limits);
        Wire.ExecutionReport.Builder root = Wire.ExecutionReport.InitRoot(message);
        root.SetVersion(1);
        root.SetWorkspaceId(value.WorkspaceId);
        root.SetOperationId(value.OperationId);
        root.SetRequestDigest(value.RequestDigest);
        root.SetWorkflowDocumentId(value.WorkflowDocumentId);
        root.SetPlanDocumentId(value.PlanDocumentId);
        root.SetDispatchIdentity(value.DispatchIdentity);
        root.SetRunDirectory(value.RunDirectory);
        root.SetExitCode(value.ExitCode);
        Write(root.InitReport(), value.Report);
        if (value.OperatorCancellationRequestedAt is { } cancellation)
        {
            Write(root.InitOperatorCancellationRequestedAt(), cancellation);
        }

        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static NativeExecutionReport Decode(ReadOnlySpan<byte> frame)
    {
        Wire.ExecutionReport.Reader root = Wire.ExecutionReport.ReadRoot(FrameCodec.Read(frame, Limits));
        if (root.Version != 1 || !root.HasReport)
        {
            throw new InvalidDataException("Unsupported terminal report schema.");
        }

        NativeExecutionReport report = new(Required(root.DecodeWorkspaceId()), Required(root.DecodeOperationId()),
            Required(root.DecodeRequestDigest()), Required(root.DecodeWorkflowDocumentId()),
            Required(root.DecodePlanDocumentId()), Required(root.DecodeDispatchIdentity()),
            Required(root.DecodeRunDirectory()), root.ExitCode, ReadWorkflowReport(root.Report),
            root.HasOperatorCancellationRequestedAt ? ReadTimestamp(root.OperatorCancellationRequestedAt) : null);
        Validate(report);
        return report;
    }

    private static void Validate(NativeExecutionReport evidence)
    {
        string status = evidence.ExitCode switch
        {
            0 => "succeeded",
            1 => "failed",
            130 => "canceled",
            _ => throw new InvalidDataException("Invalid terminal exit code.")
        };
        WorkflowReport report = evidence.Report;
        if (string.IsNullOrWhiteSpace(evidence.WorkspaceId) || !IsHex(evidence.OperationId, 32) ||
            !IsHex(evidence.RequestDigest, 64) || !IsHex(evidence.WorkflowDocumentId, 64) ||
            !IsHex(evidence.PlanDocumentId, 64) || !IsHex(evidence.DispatchIdentity, 64) ||
            !Path.IsPathFullyQualified(evidence.RunDirectory) || report.SchemaVersion != 1 ||
            !IsHex(report.ManifestHash, 64) || report.FinishedAt is null || report.Status != status ||
            report.ResumeCount < 0 || report.Nodes.Select(node => node.Id).Distinct(StringComparer.Ordinal).Count() != report.Nodes.Count)
        {
            throw new InvalidDataException("Invalid terminal report identity or outcome.");
        }
    }

    private static bool IsHex(string text, int length) => text.Length == length && text.All(Uri.IsHexDigit);
    private static string Required(string? value) => value ?? throw new InvalidDataException("Missing terminal report text.");

    private static void Write(Wire.Timestamp.Builder wire, DateTimeOffset value)
    {
        wire.SetTicks(value.Ticks);
        wire.SetOffsetMinutes(checked((short)value.Offset.TotalMinutes));
    }

    private static DateTimeOffset ReadTimestamp(Wire.Timestamp.Reader wire) =>
        new(wire.Ticks, TimeSpan.FromMinutes(wire.OffsetMinutes));
}
