using System.Diagnostics;

internal sealed class SshTestPrograms : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "cmw-ssh-" + Guid.NewGuid().ToString("N")[..10]);
    internal string Arguments => Path.Combine(Root, "arguments");
    internal string PidFile => Path.Combine(Root, "pid");

    internal SshTestPrograms() => Directory.CreateDirectory(Root);

    internal string Script(string name, string body)
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("POSIX SSH-shell test fixture.");
        var path = Path.Combine(Root, name);
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
        if (!File.Exists(pidFile)) throw new Exception("Fixture never started its process.");
        var pid = int.Parse(File.ReadAllText(pidFile));
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) throw new Exception("SSH process was not reaped.");
        }
        catch (ArgumentException) { }
    }

    public void Dispose() => Directory.Delete(Root, true);
}
