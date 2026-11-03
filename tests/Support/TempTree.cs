using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CMakeWorkflow.IntegrationTests;

internal sealed class TempTree : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "cmake-workflow-test-" + Guid.NewGuid().ToString("N"));

    public TempTree() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
