// Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Tooling.Environment;
#pragma warning restore IDE0130

public interface IWindowsEtwProbe
{
    EtwProbeResult Probe(string capability, CancellationToken cancellationToken);
}
