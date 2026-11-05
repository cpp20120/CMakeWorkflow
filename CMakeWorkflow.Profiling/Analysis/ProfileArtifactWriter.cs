#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Profiling;
#pragma warning restore IDE0130

internal static class ProfileArtifactWriter
{
    public static async Task WriteTextAsync(string path, string text, CancellationToken ct)
    {
        _ = Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
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