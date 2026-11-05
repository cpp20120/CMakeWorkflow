using System.Diagnostics;

internal sealed class SshTestPrograms : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cmw-ssh-" + Guid.NewGuid().ToString("N")[..10]);
    internal string Arguments => Path.Combine(Root, "arguments");
    internal string PidFile => Path.Combine(Root, "pid");

    internal SshTestPrograms()
    {
        _ = Directory.CreateDirectory(Root);
    }

    internal string Script(string name, string body)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("POSIX SSH-shell test fixture.");
        }

        string path = Path.Combine(Root, name);
        File.WriteAllText(path, "#!/bin/sh\nset -eu\n" + body + "\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal string Ssh() => Script("ssh", $"""
        printf '%s\n' "$@" > {Quote(Arguments)}
        printf '%s' "$$" > {Quote(PidFile)}
        test "$1" = '-T'
        test "$2" = '-o'
        test "$3" = 'BatchMode=yes'
        test "$4" = '--'
        shift 5
        test "$#" = '1'
        exec /bin/sh -c "$1"
        """);

    internal string Peer() => Script("host ' with spaces;$HOME", $"""
        exec dotnet {Quote(typeof(SshTestPrograms).Assembly.Location)} --ssh-peer "$@"
        """);

    internal static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    internal void AssertExited(string? pidFile = null)
    {
        pidFile ??= PidFile;
        if (!File.Exists(pidFile))
        {
            throw new UnreachableException("Fixture never started its process.");
        }

        int pid = int.Parse(File.ReadAllText(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        try
        {
            using Process process = Process.GetProcessById(pid);
            if (!process.HasExited)
            {
                throw new UnreachableException("SSH process was not reaped.");
            }
        }
        catch (ArgumentException) { }
    }

    // An orphaned grandchild cannot be reaped by the SSH transport. It must
    // stop executing, but may briefly remain a zombie under its new parent.
    internal static async Task AssertStoppedAsync(string pidFile, string startStatFile)
    {
        if (!File.Exists(pidFile))
        {
            throw new UnreachableException("Fixture never started its descendant.");
        }

        int pid = int.Parse(await File.ReadAllTextAsync(pidFile), System.Globalization.CultureInfo.InvariantCulture);
        string? originalStartTime = OperatingSystem.IsLinux()
            ? ReadLinuxStat(await File.ReadAllTextAsync(startStatFile)).StartTime
            : null;
        Stopwatch watch = Stopwatch.StartNew();
        while (watch.Elapsed < TimeSpan.FromSeconds(2))
        {
            if (OperatingSystem.IsLinux())
            {
                string statPath = $"/proc/{pid}/stat";
                string? stat;
                try
                {
                    stat = await File.ReadAllTextAsync(statPath);
                }
                catch (FileNotFoundException)
                {
                    return;
                }
                catch (DirectoryNotFoundException)
                {
                    return;
                }

                (char state, string startTime) = ReadLinuxStat(stat);
                if (startTime != originalStartTime || state is 'Z' or 'X' or 'x')
                {
                    return;
                }
            }
            else
            {
                try
                {
                    using Process process = Process.GetProcessById(pid);
                    if (process.HasExited)
                    {
                        return;
                    }
                }
                catch (ArgumentException)
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        throw new UnreachableException($"SSH descendant PID {pid} is still running.");
    }

    private static (char State, string StartTime) ReadLinuxStat(string stat)
    {
        // The comm field may contain spaces and parentheses. Field 3 (state)
        // starts after its last ')'; field 22 (starttime) is index 19 there.
        int closingParen = stat.LastIndexOf(')');
        if (closingParen < 0 || stat.Length <= closingParen + 2)
        {
            throw new InvalidDataException("Invalid Linux process stat.");
        }

        string[] fields = stat[(closingParen + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length > 19
            ? (fields[0][0], fields[19])
            : throw new InvalidDataException("Incomplete Linux process stat.");
    }

    public void Dispose() => Directory.Delete(Root, true);
}
