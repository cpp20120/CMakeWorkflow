namespace CMakeWorkflow.Profiling.Analysis;

/// <summary>Publishes analysis text by moving a unique temporary sibling over its destination.</summary>
/// <remarks>
/// This is artifact publication, not a durable write-ahead checkpoint: no file or directory sync is requested.
/// No writer lease is acquired; callers coordinate concurrent analysis of the same output.
/// </remarks>
internal static class ProfileArtifactWriter
{
    /// <summary>Writes text to a temporary sibling and replaces the destination after a cancellation check.</summary>
    /// <remarks>
    /// Creates the parent directory. Cancellation before replacement can leave an existing destination unchanged;
    /// a cancellation racing after the check does not prevent publication. Finally removes a remaining temporary
    /// file, and cleanup failure can propagate. No rollback of a completed move is attempted.
    /// </remarks>
    /// <param name="path">Analysis artifact destination with a parent directory.</param>
    /// <param name="text">Complete replacement text.</param>
    /// <param name="ct">Write/check cancellation; not execution cancellation for the capture being analyzed.</param>
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