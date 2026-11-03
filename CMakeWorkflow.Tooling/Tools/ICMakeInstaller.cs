namespace CMakeWorkflow.Tooling;

public interface ICMakeInstaller
{
    Task InstallAsync(string installDirectory, CancellationToken ct);
}