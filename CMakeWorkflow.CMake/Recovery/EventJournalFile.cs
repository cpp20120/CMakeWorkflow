namespace CMakeWorkflow.CMake.Recovery;

internal static class EventJournalFile
{
    internal static async Task<StreamWriter> OpenAsync(string path, Action<string>? restore)
    {
        bool separateTail = false;
        if (restore is not null && File.Exists(path))
        {
            await using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(input, leaveOpen: true);
            while (await reader.ReadLineAsync() is { } line)
            {
                restore(line);
            }

            if (input.Length > 0)
            {
                byte[] last = new byte[1];
                _ = input.Seek(-1, SeekOrigin.End);
                await input.ReadExactlyAsync(last);
                separateTail = last[0] != '\n';
            }
        }

        StreamWriter writer = new(new FileStream(path, FileMode.Append, FileAccess.Write,
            FileShare.ReadWrite, 4096, FileOptions.Asynchronous));
        try
        {
            if (separateTail)
            {
                // Preserve a truncated final event as a separate historical line.
                await AppendAsync(writer, "");
            }

            return writer;
        }
        catch
        {
            await CloseAsync(writer);
            throw;
        }
    }

    internal static async Task AppendAsync(StreamWriter writer, string line)
    {
        await writer.WriteLineAsync(line);
        await writer.FlushAsync();
    }

    internal static async ValueTask CloseAsync(StreamWriter? writer)
    {
        if (writer is not null)
        {
            try { await writer.DisposeAsync(); }
            catch (Exception error) when (error is IOException or ObjectDisposedException) { }
        }
    }
}
