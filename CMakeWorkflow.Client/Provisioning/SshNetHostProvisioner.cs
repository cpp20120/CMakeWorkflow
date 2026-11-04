using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;

namespace CMakeWorkflow.Client;

/// <summary>
/// Opt-in deployment of an already published Linux Host. Builds a deterministic
/// plan, checks an explicit approval digest, uploads via pinned SFTP, verifies
/// bytes on the remote machine and atomically activates a versioned directory.
/// Never invokes sudo or installs system packages. No persistent Host is killed.
/// The exclusive directory lock fails closed after a crashed installer: remove
/// it manually only after confirming that no installer is still running.
/// </summary>
public static class SshNetHostProvisioner
{
    private const string ExecutableName = "CMakeWorkflow";

    public static SshNetProvisionPlan Plan(SshNetProvisionOptions options, CancellationToken ct = default)
    {
        Validate(options);
        ct.ThrowIfCancellationRequested();
        var files = DiscoverFiles(options.PublishDirectory, ct);
        var manifest = Manifest(files);
        var releaseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        var release = options.InstallRoot + "/versions/" + releaseHash;
        var expected = options.RuntimeIdentifier == "linux-x64" ? "x86_64" : "aarch64";

        using var key = new PrivateKeyFile(options.PrivateKeyPath);
        using var ssh = CreateSsh(options, key);
        ssh.Connect();
        ct.ThrowIfCancellationRequested();
        var system = Run(ssh, "uname -s");
        var machine = Run(ssh, "uname -m");
        if (system != "Linux" || machine != expected)
            throw new InvalidOperationException($"Remote target is {system}/{machine}; supplied publish RID is {options.RuntimeIdentifier}.");
        Run(ssh, "test -d " + Q(options.Workspace) + " && test -w " + Q(options.Workspace) +
                 " && command -v sha256sum >/dev/null && command -v mv >/dev/null && command -v stat >/dev/null");
        AssertInstallRoot(ssh, options.InstallRoot);

        var tools = new[] { "cmake", "ctest", "ninja", "c++" }.Select(tool =>
            tool + ": " + Run(ssh, "if command -v " + tool + " >/dev/null 2>&1; then command -v " + tool +
                "; else printf 'missing\\n'; fi")).ToArray();
        var active = Run(ssh, "if [ -L " + Q(options.InstallRoot + "/current") + " ]; then readlink -- " +
            Q(options.InstallRoot + "/current") + "; elif [ -e " + Q(options.InstallRoot + "/current") +
            " ]; then exit 65; fi");
        var digest = Approval(options, manifest);
        return new(digest, options.RuntimeIdentifier, machine, options.InstallRoot,
            release, options.InstallRoot + "/current/" + ExecutableName,
            active == "versions/" + releaseHash, tools, files);
    }

    public static SshNetProvisionReceipt Apply(SshNetProvisionOptions options, string approvedSha256,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(approvedSha256);
        if (approvedSha256.Length != 64 || !approvedSha256.All(Uri.IsHexDigit))
            throw new ArgumentException("The approval digest must be 64 hexadecimal characters.", nameof(approvedSha256));
        // Re-plan before any mutations. In particular an edited local binary or
        // changed target rejects the previous approval before creating a directory.
        var plan = Plan(options, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(approvedSha256), Convert.FromHexString(plan.ApprovalSha256)))
            throw new InvalidOperationException("Provision plan changed; review and approve a new plan.");
        ct.ThrowIfCancellationRequested();
        using var key = new PrivateKeyFile(options.PrivateKeyPath);
        using var ssh = CreateSsh(options, key);
        ssh.Connect();
        var root = options.InstallRoot;
        // No privileged writes. Check any existing destination BEFORE chmod or
        // mkdir, then verify ownership again after creating it.
        AssertInstallRoot(ssh, root);
        Run(ssh, "mkdir -p -- " + Q(root) + " && chmod 700 -- " + Q(root));
        AssertInstallRoot(ssh, root);
        Run(ssh, "mkdir -p -- " + Q(root + "/versions") +
            " && test ! -L " + Q(root + "/versions"));
        var locked = false;
        string? staging = null;
        try
        {
            // Exclusive creation, modeled after CasinoShiz.Deploy's activation
            // lease. A stale lock is intentionally NOT stolen automatically.
            Run(ssh, "mkdir -m 700 -- " + Q(root + "/.bootstrap-lock"));
            locked = true;
            ct.ThrowIfCancellationRequested();
            var versionExists = Run(ssh, "if [ -d " + Q(plan.ReleaseDirectory) + " ] && [ ! -L " +
                Q(plan.ReleaseDirectory) + " ]; then printf 'yes\\n'; elif [ -e " + Q(plan.ReleaseDirectory) +
                " ] || [ -L " + Q(plan.ReleaseDirectory) + " ]; then exit 66; fi") == "yes";
            if (!versionExists)
            {
                staging = root + "/versions/.stage-" + Guid.NewGuid().ToString("N");
                Run(ssh, "mkdir -m 700 -- " + Q(staging));
                using (var sftpKey = new PrivateKeyFile(options.PrivateKeyPath))
                using (var sftp = CreateSftp(options, sftpKey))
                {
                    sftp.Connect();
                    sftp.OperationTimeout = TimeSpan.FromMinutes(2);
                    foreach (var file in plan.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        var fullPath = Path.Combine(options.PublishDirectory,
                            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        // Do not silently deploy bytes that changed since plan.
                        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                            throw new InvalidDataException("Publish asset changed into a symlink: " + file.RelativePath);
                        using var source = File.OpenRead(fullPath);
                        var hash = Convert.ToHexString(SHA256.HashData(source));
                        if (hash != file.Sha256 || source.Length != file.Size)
                            throw new IOException("Published artifact changed while deploying: " + file.RelativePath);
                        source.Position = 0;
                        var parent = file.RelativePath.Split('/');
                        var dir = staging;
                        for (var i = 0; i < parent.Length - 1; i++)
                        {
                            dir += "/" + parent[i];
                            if (!sftp.Exists(dir)) sftp.CreateDirectory(dir);
                        }
                        sftp.UploadFile(source, staging + "/" + file.RelativePath, canOverride: false);
                    }
                    using var manifest = new MemoryStream(Encoding.UTF8.GetBytes(Manifest(plan.Files)));
                    sftp.UploadFile(manifest, staging + "/.files.sha256", canOverride: false);
                }
                // GNU sha256sum -c checks every byte before any activation.
                Verify(ssh, staging);
                Run(ssh, "chmod -R u+rwX -- " + Q(staging) + " && chmod u+x -- " +
                    Q(staging + "/" + ExecutableName));
                // A concurrent or interrupted installer must never overwrite a
                // populated version directory. The lock covers the check and mv.
                Run(ssh, "test ! -e " + Q(plan.ReleaseDirectory) + " && test ! -L " +
                    Q(plan.ReleaseDirectory) + " && mv -T -- " + Q(staging) + " " + Q(plan.ReleaseDirectory));
                staging = null;
            }
            Verify(ssh, plan.ReleaseDirectory);
            ct.ThrowIfCancellationRequested();
            var current = root + "/current";
            var active = Run(ssh, "if [ -L " + Q(current) + " ]; then readlink -- " + Q(current) +
                "; elif [ -e " + Q(current) + " ]; then exit 67; fi");
            var same = active == "versions/" + Path.GetFileName(plan.ReleaseDirectory);
            if (!same)
            {
                var newLink = root + "/.current-" + Guid.NewGuid().ToString("N");
                // Relative symlink preserves installation portability, while
                // mv -T replaces the link atomically without disturbing live PIDs.
                Run(ssh, "ln -s -- " + Q("versions/" + Path.GetFileName(plan.ReleaseDirectory)) +
                    " " + Q(newLink) + " && mv -Tf -- " + Q(newLink) + " " + Q(current));
            }
            return new(plan.ApprovalSha256, plan.ReleaseDirectory, plan.Executable, same);
        }
        finally
        {
            if (staging is not null)
                TryRun(ssh, "rm -rf -- " + Q(staging)); // Only our random stage.
            if (locked)
                TryRun(ssh, "rmdir -- " + Q(root + "/.bootstrap-lock"));
        }
    }

    private static void Validate(SshNetProvisionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SshNetConnection.Validate(new SshNetLaunchOptions(options.Host, options.Username,
            options.PrivateKeyPath, options.HostKeySha256, options.Workspace)
        {
            Port = options.Port, ConnectionTimeout = options.ConnectionTimeout
        });
        SshCommand.RemotePath(options.InstallRoot, nameof(options.InstallRoot));
        if (options.InstallRoot is "/" or "/tmp" || options.InstallRoot.EndsWith('/'))
            throw new ArgumentException("Use a dedicated remote install directory.", nameof(options.InstallRoot));
        if (options.RuntimeIdentifier is not ("linux-x64" or "linux-arm64"))
            throw new ArgumentException("Supported Linux Host RIDs: linux-x64 and linux-arm64.", nameof(options.RuntimeIdentifier));
        if (!Path.IsPathFullyQualified(options.PublishDirectory) || !Directory.Exists(options.PublishDirectory) ||
            new DirectoryInfo(options.PublishDirectory).LinkTarget is not null)
            throw new ArgumentException("Specify an existing, nonsymlink, absolute local publish directory.", nameof(options.PublishDirectory));
    }

    private static SshNetProvisionFile[] DiscoverFiles(string root, CancellationToken ct)
    {
        var found = new List<SshNetProvisionFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Published files must not contain symlinks: " + entry);
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                if ((attributes & FileAttributes.Normal) == 0 && (attributes & FileAttributes.Device) != 0)
                    throw new InvalidDataException("Published artifact is not a regular file: " + entry);
                var relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative == ".files.sha256" || relative.Split('/').Any(x => x is "" or "." or "..") ||
                    relative.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+')))
                    throw new InvalidDataException("Unsupported publish asset name: " + relative);
                using var input = File.OpenRead(entry);
                found.Add(new(relative, Convert.ToHexString(SHA256.HashData(input)), input.Length));
                if (found.Count > 1024) throw new InvalidDataException("Too many publish assets.");
            }
        }
        found.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        if (!found.Any(f => f.RelativePath == ExecutableName))
            throw new InvalidDataException("Publish directory must contain the Linux CMakeWorkflow executable.");
        return found.ToArray();
    }

    private static string Manifest(IEnumerable<SshNetProvisionFile> files) => string.Concat(
        files.Select(file => file.Sha256.ToLowerInvariant() + "  ./" + file.RelativePath + "\n"));

    private static string Approval(SshNetProvisionOptions options, string manifest)
    {
        var identity = string.Join('\n', "CMakeWorkflow F2 provisioning v1", options.Host,
            options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), options.Username,
            options.HostKeySha256, options.Workspace, options.InstallRoot, options.RuntimeIdentifier, manifest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static SshClient CreateSsh(SshNetProvisionOptions options, PrivateKeyFile key)
    {
        var ssh = new SshClient(new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
            { Timeout = options.ConnectionTimeout });
        Pin(ssh, options.HostKeySha256);
        return ssh;
    }

    private static SftpClient CreateSftp(SshNetProvisionOptions options, PrivateKeyFile key)
    {
        var sftp = new SftpClient(new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
            { Timeout = options.ConnectionTimeout });
        Pin(sftp, options.HostKeySha256);
        return sftp;
    }

    private static void Pin(BaseClient client, string provided)
    {
        var pin = provided.StartsWith("SHA256:", StringComparison.Ordinal) ? provided[7..] : provided;
        var expected = Convert.FromBase64String(pin + "=");
        client.HostKeyReceived += (_, args) =>
            args.CanTrust = CryptographicOperations.FixedTimeEquals(SHA256.HashData(args.HostKey), expected);
    }

    private static void AssertInstallRoot(SshClient ssh, string root) => Run(ssh,
        "if [ -e " + Q(root) + " ] || [ -L " + Q(root) + " ]; then " +
        "test ! -L " + Q(root) + " && test -d " + Q(root) +
        " && test \"$(stat -c %u -- " + Q(root) + ")\" = \"$(id -u)\"; fi");

    private static void Verify(SshClient ssh, string directory) => Run(ssh,
        "cd -- " + Q(directory) + " && sha256sum --check --status .files.sha256");

    private static string Run(SshClient ssh, string command)
    {
        using var remote = ssh.CreateCommand(command);
        remote.CommandTimeout = TimeSpan.FromSeconds(30);
        var result = remote.Execute();
        if (remote.ExitStatus != 0)
            throw new IOException($"Remote preflight/provisioning command failed (exit {remote.ExitStatus}): " +
                remote.Error.Trim());
        return result.Trim();
    }

    private static void TryRun(SshClient ssh, string command)
    {
        try { if (ssh.IsConnected) Run(ssh, command); }
        catch (Exception error) when (error is IOException or Renci.SshNet.Common.SshException)
        {
            // Teardown must not conceal the primary failure. A stale lock is
            // visible and requires manual recovery; it is never stolen.
        }
    }

    private static string Q(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
