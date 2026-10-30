namespace CMakeWorkflow.Host;

// Serializes Host installations for this user, including separate Host processes.
// Native package managers retain their own locks; ordinary tooling setup is not
// covered by this Host-specific lock. Never delete the lock file (inode races).
public sealed class FixCoordinator(string? lockDirectory = null) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly string directory = lockDirectory ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CMakeWorkflow", "host-fix");

    public IDisposable? TryAcquire()
    {
        if (!gate.Wait(0)) return null;
        try
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else
            {
                Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                if (new DirectoryInfo(directory).LinkTarget is not null
                    || (File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
                    throw new IOException("Host fix lock directory must not be a symlink or writable by other users.");
            }
            var file = new FileStream(Path.Combine(directory, "installation.lock"), FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
            return new Lease(file, gate);
        }
        catch (IOException) { gate.Release(); return null; }
        catch { gate.Release(); throw; }
    }
    public void Dispose() => gate.Dispose();
    private sealed class Lease(FileStream file, SemaphoreSlim gate) : IDisposable
    {
        public void Dispose() { file.Dispose(); gate.Release(); }
    }
}
