using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling;

public interface IWindowsEtwProbe
{
    EtwProbeResult Probe(string capability, CancellationToken cancellationToken);
}
