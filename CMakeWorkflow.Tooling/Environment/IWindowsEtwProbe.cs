namespace CMakeWorkflow.Tooling.Environment;

public interface IWindowsEtwProbe
{
    EtwProbeResult Probe(string capability, CancellationToken cancellationToken);
}
