using System.Text;
using System.Text.Json;

namespace CMakeWorkflow;

public static class WorkflowManifestBytes
{
    public static byte[] Serialize(WorkflowDocument document) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document,
            WorkflowJson.Default.WorkflowDocument) + "\n");

    public static async Task WriteNewAsync(string output, WorkflowDocument document,
        CancellationToken ct)
    {
        var bytes = Serialize(document);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(output) || Directory.Exists(output))
            throw new IOException($"Manifest already exists: {output}");
        var parent = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(parent);
        var temp = Path.Combine(parent, ".workflow-recipe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temp, output);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
