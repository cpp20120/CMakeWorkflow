using System.Text;
using CMakeWorkflow.Host;

// No Host, network, or CMake needed: exercises the actual cross-platform
// descriptor-relative artifact opener, including held-handle path replacement.
internal static class ArtifactPathBoundaryTests
{
    private static void Check(bool condition, string why)
    {
        if (!condition) throw new Exception(why);
    }

    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "cmwf-artifact-path-" + Guid.NewGuid().ToString("N"));
        var directory = Path.Combine(root, "results", "logs");
        var outside = Path.Combine(root, "private");
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(outside);
        var expected = Path.Combine(directory, "effect.stdout");
        var privateFile = Path.Combine(outside, "private.txt");
        File.WriteAllText(expected, "authorized-result", Encoding.UTF8);
        File.WriteAllText(privateFile, "never-read-this", Encoding.UTF8);
        File.WriteAllText(Path.Combine(outside, "effect.stdout"), "private-parent-redirect", Encoding.UTF8);
        try
        {
            using (var stream = OperationArtifactFile.OpenRead(root, expected))
            using (var reader = new StreamReader(stream, Encoding.UTF8))
                Check(reader.ReadToEnd() == "authorized-result", "Ordinary artifact read failed.");

            // A path outside the anchored workspace is rejected before opening.
            var external = Path.Combine(Path.GetDirectoryName(root)!, "sibling.txt");
            try
            {
                using var forbidden = OperationArtifactFile.OpenRead(root, external);
                throw new Exception("Outside workspace path accepted.");
            }
            catch (ApplicationRefusal error) when (error.Code == "path_not_allowed") { }

            // On Windows only the capability probe may be skipped. Once links
            // can be created, all rejection assertions are mandatory.
            var linksSupported = true;
            if (OperatingSystem.IsWindows())
            {
                var probe = Path.Combine(root, "symlink-probe");
                try { File.CreateSymbolicLink(probe, privateFile); }
                catch (UnauthorizedAccessException) { linksSupported = false; }
                catch (PlatformNotSupportedException) { linksSupported = false; }
                catch (IOException error) when (error.HResult == unchecked((int)0x80070522) ||
                                               error.HResult == unchecked((int)0x80070032))
                { linksSupported = false; }
                finally { if (File.Exists(probe)) File.Delete(probe); }
            }
            if (linksSupported) AssertLinks(root, directory, outside, expected, privateFile);
            else Console.WriteLine("Windows symlink capability unavailable; regular-file path test passed.");
            Console.WriteLine("PASS artifact path boundary: regular file, workspace isolation and no-follow handles");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void AssertLinks(string root, string directory, string outside,
        string expected, string privateFile)
    {
        var backup = expected + ".original";
        File.Move(expected, backup);
        try
        {
            File.CreateSymbolicLink(expected, privateFile);
            Reject(root, expected);
        }
        finally
        {
            File.Delete(expected);
            File.Move(backup, expected);
        }

        // Unix permits rename of an open directory. Windows can deny the rename
        // while a descendant file is held, even when the leaf shares deletion.
        // Check the pinned handle in both cases; if Windows blocks the rename,
        // test the redirect immediately after the handle has been released.
        var moved = directory + ".moved";
        var movedWhileHeld = false;
        try
        {
            using (var held = OperationArtifactFile.OpenRead(root, expected))
            {
                try
                {
                    Directory.Move(directory, moved);
                    movedWhileHeld = true;
                }
                catch (IOException error) when (OperatingSystem.IsWindows() &&
                                               (error.HResult == unchecked((int)0x80070005) ||
                                                error.HResult == unchecked((int)0x80070020) ||
                                                error.HResult == unchecked((int)0x80070021)))
                {
                    // Access denied / sharing or lock violation: this is a Windows
                    // filesystem constraint, not a failure of no-follow opening.
                }

                if (movedWhileHeld)
                {
                    Directory.CreateSymbolicLink(directory, outside);
                    Reject(root, expected);
                }
                using var reader = new StreamReader(held, Encoding.UTF8, leaveOpen: true);
                Check(reader.ReadToEnd() == "authorized-result", "Pinned file handle was redirected.");
            }
        }
        finally
        {
            // A successful rename may still leave the reverse rename blocked
            // until the held leaf is closed (Windows).
            if (movedWhileHeld) RestoreDirectory(directory, moved);
        }

        if (!movedWhileHeld)
        {
            // Still exercise the redirected-parent rejection on Windows, but
            // don't require a rename that the OS forbids with an open child.
            Directory.Move(directory, moved);
            try
            {
                Directory.CreateSymbolicLink(directory, outside);
                Reject(root, expected);
            }
            finally
            {
                RestoreDirectory(directory, moved);
            }
        }
    }

    private static void RestoreDirectory(string directory, string moved)
    {
        if (Directory.Exists(directory) || File.Exists(directory)) Directory.Delete(directory);
        Directory.Move(moved, directory);
    }

    private static void Reject(string root, string path)
    {
        try
        {
            using var stream = OperationArtifactFile.OpenRead(root, path);
            throw new Exception("Symlink/junction artifact was opened.");
        }
        catch (ApplicationRefusal error) when (error.Code == "path_not_allowed") { }
        catch (IOException) { /* OS no-follow open can return ELOOP/NTSTATUS. */ }
    }
}
