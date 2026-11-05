using System.Security.Cryptography;
using System.Text;
using CMakeWorkflow.Client.Transport;
using Renci.SshNet;

namespace CMakeWorkflow.Client.Provisioning;

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
    private static readonly string[] ProbeToolNames =
    [
    "cmake",
    "ctest",
    "ninja",
    "c++"
];
    private const string ExecutableName = "CMakeWorkflow";

    public static SshNetProvisionPlan Plan(SshNetProvisionOptions options, CancellationToken ct = default)
    {
        Validate(options);
        ct.ThrowIfCancellationRequested();
        SshNetProvisionFile[] files = DiscoverFiles(options.PublishDirectory, ct);
        string manifest = Manifest(files);
        string releaseHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(manifest)));
        string release = options.InstallRoot + "/versions/" + releaseHash;
        string expected = options.RuntimeIdentifier == "linux-x64" ? "x86_64" : "aarch64";

        using PrivateKeyFile key = new(options.PrivateKeyPath);
        using SshClient ssh = CreateSsh(options, key);
        ssh.Connect();
        ct.ThrowIfCancellationRequested();
        string system = Run(ssh, "uname -s");
        string machine = Run(ssh, "uname -m");
        if (system != "Linux" || machine != expected)
        {
            throw new InvalidOperationException($"Remote target is {system}/{machine}; supplied publish RID is {options.RuntimeIdentifier}.");
        }

        _ = Run(ssh, "test -d " + Q(options.Workspace) + " && test -w " + Q(options.Workspace) +
                 " && command -v sha256sum >/dev/null && command -v mv >/dev/null && command -v stat >/dev/null");
        AssertInstallRoot(ssh, options.InstallRoot);

        string[] tools = [.. ProbeToolNames.Select(tool =>
            tool + ": " + Run(ssh, "if command -v " + tool + " >/dev/null 2>&1; then command -v " + tool +
                "; else printf 'missing\\n'; fi"))];
        string active = Run(ssh, "if [ -L " + Q(options.InstallRoot + "/current") + " ]; then readlink -- " +
            Q(options.InstallRoot + "/current") + "; elif [ -e " + Q(options.InstallRoot + "/current") +
            " ]; then exit 65; fi");
        string digest = Approval(options, manifest);
        return new(digest, options.RuntimeIdentifier, machine, options.InstallRoot,
            release, options.InstallRoot + "/current/" + ExecutableName,
            active == "versions/" + releaseHash, tools, files);
    }

    public static SshNetProvisionReceipt Apply(SshNetProvisionOptions options, string approvedSha256,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(approvedSha256);
        if (approvedSha256.Length != 64 || !approvedSha256.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("The approval digest must be 64 hexadecimal characters.", nameof(approvedSha256));
        }
        // Re-plan before any mutations. In particular an edited local binary or
        // changed target rejects the previous approval before creating a directory.
        SshNetProvisionPlan plan = Plan(options, ct);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(approvedSha256), Convert.FromHexString(plan.ApprovalSha256)))
        {
            throw new InvalidOperationException("Provision plan changed; review and approve a new plan.");
        }

        ct.ThrowIfCancellationRequested();
        using PrivateKeyFile key = new(options.PrivateKeyPath);
        using SshClient ssh = CreateSsh(options, key);
        ssh.Connect();
        string root = options.InstallRoot;
        // No privileged writes. Check any existing destination BEFORE chmod or
        // mkdir, then verify ownership again after creating it.
        AssertInstallRoot(ssh, root);
        _ = Run(ssh, "mkdir -p -- " + Q(root) + " && chmod 700 -- " + Q(root));
        AssertInstallRoot(ssh, root);
        _ = Run(ssh, "mkdir -p -- " + Q(root + "/versions") +
            " && test ! -L " + Q(root + "/versions"));
        bool locked = false;
        string? staging = null;
        try
        {
            // Exclusive creation, modeled after CasinoShiz.Deploy's activation
            // lease. A stale lock is intentionally NOT stolen automatically.
            _ = Run(ssh, "mkdir -m 700 -- " + Q(root + "/.bootstrap-lock"));
            locked = true;
            ct.ThrowIfCancellationRequested();
            bool versionExists = Run(ssh, "if [ -d " + Q(plan.ReleaseDirectory) + " ] && [ ! -L " +
                Q(plan.ReleaseDirectory) + " ]; then printf 'yes\\n'; elif [ -e " + Q(plan.ReleaseDirectory) +
                " ] || [ -L " + Q(plan.ReleaseDirectory) + " ]; then exit 66; fi") == "yes";
            if (!versionExists)
            {
                staging = root + "/versions/.stage-" + Guid.NewGuid().ToString("N");
                _ = Run(ssh, "mkdir -m 700 -- " + Q(staging));
                using (PrivateKeyFile sftpKey = new(options.PrivateKeyPath))
                using (SftpClient sftp = CreateSftp(options, sftpKey))
                {
                    sftp.Connect();
                    sftp.OperationTimeout = TimeSpan.FromMinutes(2);
                    foreach (SshNetProvisionFile file in plan.Files)
                    {
                        ct.ThrowIfCancellationRequested();
                        string fullPath = Path.Combine(options.PublishDirectory,
                            file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                        // Do not silently deploy bytes that changed since plan.
                        if ((File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new InvalidDataException("Publish asset changed into a symlink: " + file.RelativePath);
                        }

                        using FileStream source = File.OpenRead(fullPath);
                        string hash = Convert.ToHexString(SHA256.HashData(source));
                        if (hash != file.Sha256 || source.Length != file.Size)
                        {
                            throw new IOException("Published artifact changed while deploying: " + file.RelativePath);
                        }

                        source.Position = 0;
                        string[] parent = file.RelativePath.Split('/');
                        string dir = staging;
                        for (int i = 0; i < parent.Length - 1; i++)
                        {
                            dir += "/" + parent[i];
                            if (!sftp.Exists(dir))
                            {
                                sftp.CreateDirectory(dir);
                            }
                        }
                        sftp.UploadFile(source, staging + "/" + file.RelativePath, canOverride: false);
                    }
                    using MemoryStream manifest = new(Encoding.UTF8.GetBytes(Manifest(plan.Files)));
                    sftp.UploadFile(manifest, staging + "/.files.sha256", canOverride: false);
                }
                // GNU sha256sum -c checks every byte before any activation.
                Verify(ssh, staging);
                _ = Run(ssh, "chmod -R u+rwX -- " + Q(staging) + " && chmod u+x -- " +
                    Q(staging + "/" + ExecutableName));
                // A concurrent or interrupted installer must never overwrite a
                // populated version directory. The lock covers the check and mv.
                _ = Run(ssh, "test ! -e " + Q(plan.ReleaseDirectory) + " && test ! -L " +
                    Q(plan.ReleaseDirectory) + " && mv -T -- " + Q(staging) + " " + Q(plan.ReleaseDirectory));
                staging = null;
            }
            Verify(ssh, plan.ReleaseDirectory);
            ct.ThrowIfCancellationRequested();
            string current = root + "/current";
            string active = Run(ssh, "if [ -L " + Q(current) + " ]; then readlink -- " + Q(current) +
                "; elif [ -e " + Q(current) + " ]; then exit 67; fi");
            bool same = active == "versions/" + Path.GetFileName(plan.ReleaseDirectory);
            if (!same)
            {
                string newLink = root + "/.current-" + Guid.NewGuid().ToString("N");
                // Relative symlink preserves installation portability, while
                // mv -T replaces the link atomically without disturbing live PIDs.
                _ = Run(ssh, "ln -s -- " + Q("versions/" + Path.GetFileName(plan.ReleaseDirectory)) +
                    " " + Q(newLink) + " && mv -Tf -- " + Q(newLink) + " " + Q(current));
            }
            return new(plan.ApprovalSha256, plan.ReleaseDirectory, plan.Executable, same);
        }
        finally
        {
            if (staging is not null)
            {
                TryRun(ssh, "rm -rf -- " + Q(staging)); // Only our random stage.
            }

            if (locked)
            {
                TryRun(ssh, "rmdir -- " + Q(root + "/.bootstrap-lock"));
            }
        }
    }

    private static void Validate(SshNetProvisionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = SshNetConnection.Validate(new SshNetLaunchOptions(options.Host, options.Username,
            options.PrivateKeyPath, options.HostKeySha256, options.Workspace)
        {
            Port = options.Port,
            ConnectionTimeout = options.ConnectionTimeout
        });
        global::CMakeWorkflow.Client.Transport.SshCommand.RemotePath(options.InstallRoot, nameof(options.InstallRoot));
        if (options.InstallRoot is "/" or "/tmp" || options.InstallRoot.EndsWith('/'))
        {
            throw new ArgumentException("Use a dedicated remote install directory.", nameof(options));
        }

        if (options.RuntimeIdentifier is not ("linux-x64" or "linux-arm64"))
        {
            throw new ArgumentException("Supported Linux Host RIDs: linux-x64 and linux-arm64.", nameof(options));
        }

        if (!Path.IsPathFullyQualified(options.PublishDirectory) || !Directory.Exists(options.PublishDirectory) ||
            new DirectoryInfo(options.PublishDirectory).LinkTarget is not null)
        {
            throw new ArgumentException("Specify an existing, nonsymlink, absolute local publish directory.", nameof(options));
        }
    }

    private static SshNetProvisionFile[] DiscoverFiles(string root, CancellationToken ct)
    {
        List<SshNetProvisionFile> found = [];
        Stack<string> pending = new();
        pending.Push(root);
        while (pending.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidDataException("Published files must not contain symlinks: " + entry);
                }

                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(entry); continue; }
                if ((attributes & FileAttributes.Normal) == 0 && (attributes & FileAttributes.Device) != 0)
                {
                    throw new InvalidDataException("Published artifact is not a regular file: " + entry);
                }

                string relative = Path.GetRelativePath(root, entry).Replace('\\', '/');
                if (relative == ".files.sha256" || relative.Split('/').Any(x => x is "" or "." or "..") ||
                    relative.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '_' or '-' or '+')))
                {
                    throw new InvalidDataException("Unsupported publish asset name: " + relative);
                }

                using FileStream input = File.OpenRead(entry);
                found.Add(new(relative, Convert.ToHexString(SHA256.HashData(input)), input.Length));
                if (found.Count > 1024)
                {
                    throw new InvalidDataException("Too many publish assets.");
                }
            }
        }
        found.Sort((a, b) => StringComparer.Ordinal.Compare(a.RelativePath, b.RelativePath));
        return !found.Any(f => f.RelativePath == ExecutableName)
            ? throw new InvalidDataException("Publish directory must contain the Linux CMakeWorkflow executable.")
            : [.. found];
    }


#pragma warning disable CA1308 // Lowercase values are part of the existing persisted hash, identifier, or command protocol.
    private static string Manifest(IEnumerable<SshNetProvisionFile> files) => string.Concat(
            files.Select(file => file.Sha256.ToLowerInvariant() + "  ./" + file.RelativePath + "\n"));
#pragma warning restore CA1308


    private static string Approval(SshNetProvisionOptions options, string manifest)
    {
        string identity = string.Join('\n', "CMakeWorkflow F2 provisioning v1", options.Host,
            options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), options.Username,
            options.HostKeySha256, options.Workspace, options.InstallRoot, options.RuntimeIdentifier, manifest);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static SshClient CreateSsh(SshNetProvisionOptions options, PrivateKeyFile key)
    {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        SshClient ssh = new(new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
        { Timeout = options.ConnectionTimeout });
#pragma warning restore CA2000

        Pin(ssh, options.HostKeySha256);
        return ssh;
    }

    private static SftpClient CreateSftp(SshNetProvisionOptions options, PrivateKeyFile key)
    {

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        SftpClient sftp = new(new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
        { Timeout = options.ConnectionTimeout });
#pragma warning restore CA2000

        Pin(sftp, options.HostKeySha256);
        return sftp;
    }

    private static void Pin(BaseClient client, string provided)
    {
        string pin = provided.StartsWith("SHA256:", StringComparison.Ordinal) ? provided[7..] : provided;
        byte[] expected = Convert.FromBase64String(pin + "=");
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
        using Renci.SshNet.SshCommand remote = ssh.CreateCommand(command);
        remote.CommandTimeout = TimeSpan.FromSeconds(30);
        string result = remote.Execute();
        return remote.ExitStatus != 0
            ? throw new IOException($"Remote preflight/provisioning command failed (exit {remote.ExitStatus}): " +
                remote.Error.Trim())
            : result.Trim();
    }

    private static void TryRun(SshClient ssh, string command)
    {
        try
        {
            if (ssh.IsConnected)
            {
                _ = Run(ssh, command);
            }
        }
        catch (Exception error) when (error is IOException or Renci.SshNet.Common.SshException)
        {
            // Teardown must not conceal the primary failure. A stale lock is
            // visible and requires manual recovery; it is never stolen.
        }
    }

    private static string Q(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
}
