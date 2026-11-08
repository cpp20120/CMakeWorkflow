namespace CMakeWorkflow.Tooling.Tools;

public interface ICMakeInstaller
{
    Task InstallAsync(string installDirectory, CancellationToken ct);
}