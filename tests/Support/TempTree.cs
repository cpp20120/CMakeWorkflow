#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.IntegrationTests;
#pragma warning restore IDE0130

internal sealed class TempTree : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "cmake-workflow-test-" + Guid.NewGuid().ToString("N"));

    public TempTree()
    {
        _ = Directory.CreateDirectory(Path);
    }

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
