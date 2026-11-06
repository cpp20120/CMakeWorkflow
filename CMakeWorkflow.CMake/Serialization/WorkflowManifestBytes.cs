using System.Text;
using System.Text.Json;
using CMakeWorkflow.Contracts.Workflows;

namespace CMakeWorkflow.CMake.Serialization;

public static class WorkflowManifestBytes
{
    public static byte[] Serialize(WorkflowDocument document) =>
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(document,
            WorkflowJson.Default.WorkflowDocument) + "\n");

    public static async Task WriteNewAsync(string output, WorkflowDocument document,
        CancellationToken ct)
    {
        byte[] bytes = Serialize(document);
        ct.ThrowIfCancellationRequested();
        if (File.Exists(output) || Directory.Exists(output))
        {
            throw new IOException($"Manifest already exists: {output}");
        }

        string parent = Path.GetDirectoryName(output)!;
        _ = Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent, ".workflow-recipe-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream file = new(temp, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await file.WriteAsync(bytes, ct);
                await file.FlushAsync(ct);
            }

            ct.ThrowIfCancellationRequested();
            File.Move(temp, output);
        }
        finally
        {
            if (File.Exists(temp))
            {
                File.Delete(temp);
            }
        }
    }
}