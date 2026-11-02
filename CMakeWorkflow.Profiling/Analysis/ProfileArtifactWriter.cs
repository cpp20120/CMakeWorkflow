namespace CMakeWorkflow.Profiling;

internal static class ProfileArtifactWriter
{
    public static async Task WriteTextAsync(string path, string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, text, ct);
            ct.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
