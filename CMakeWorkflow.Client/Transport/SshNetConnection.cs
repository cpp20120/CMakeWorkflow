using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using SshNetCommand = Renci.SshNet.SshCommand;

namespace CMakeWorkflow.Client;

/// <summary>
/// SSH exec channel, without PTY: raw binary stdin/stdout for JSON-RPC, stderr
/// exclusively for bounded diagnostics. With ephemeral mode the channel also
/// bounds the private remote Host lifetime.
/// </summary>
internal sealed class SshNetConnection : IRemoteSshConnection
{
    private readonly SshClient client;
    private readonly PrivateKeyFile key;
    private readonly SshNetCommand command;
    private readonly Stream input;
    private readonly Task execution;
    private readonly Task stderr;
    private readonly BoundedSshDiagnostics diagnostics = new();
    private bool disposed;

    private SshNetConnection(SshClient client, PrivateKeyFile key, SshNetCommand command, Task execution,
        Stream input)
    {
        this.client = client;
        this.key = key;
        this.command = command;
        this.execution = execution;
        this.input = input;
        stderr = DrainStderrAsync();
        // Observe early channel failures even if the RPC handshake fails first.
        _ = execution.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Stream Input => command.OutputStream;
    public Stream Output => input;
    public string Diagnostics => diagnostics.Snapshot();
    public int? ExitCode => execution.IsCompleted ? command.ExitStatus : null;

    internal static byte[] Validate(SshNetHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SshCommand.RemotePath(options.Endpoint, nameof(options.Endpoint));
        return ValidateCommon(options.Host, options.Username, options.PrivateKeyPath,
            options.HostKeySha256, options.Workspace, options.RemoteExecutable,
            options.Port, options.ConnectionTimeout);
    }

    internal static byte[] Validate(SshNetLaunchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        return ValidateCommon(options.Host, options.Username, options.PrivateKeyPath,
            options.HostKeySha256, options.Workspace, options.RemoteExecutable,
            options.Port, options.ConnectionTimeout);
    }

    private static byte[] ValidateCommon(string host, string username, string privateKeyPath,
        string? hostKeySha256, string workspace, string remoteExecutable, int port,
        TimeSpan connectionTimeout)
    {
        foreach (var (value, name) in new[] {
            (host, nameof(host)), (username, nameof(username)),
            (privateKeyPath, nameof(privateKeyPath)), (remoteExecutable, nameof(remoteExecutable)) })
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
                throw new ArgumentException("Value must be nonempty and contain no control characters.", name);
        if (host.Any(char.IsWhiteSpace) || username.Any(char.IsWhiteSpace))
            throw new ArgumentException("Host and username cannot contain whitespace.");
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (!Path.IsPathFullyQualified(privateKeyPath))
            throw new ArgumentException("Private key path must be absolute on the local machine.");
        SshCommand.RemotePath(workspace, nameof(workspace));
        if (connectionTimeout <= TimeSpan.Zero || connectionTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));

        var pin = hostKeySha256;
        if (pin is null) throw new ArgumentException("A SHA256 host key pin is required.");
        if (pin.StartsWith("SHA256:", StringComparison.Ordinal)) pin = pin[7..];
        if (pin.Length != 43 || !pin.All(c => char.IsLetterOrDigit(c) || c is '+' or '/'))
            throw new ArgumentException("Host key pin must be OpenSSH SHA256:base64 (43 unpadded characters).");
        byte[] expected;
        try { expected = Convert.FromBase64String(pin + "="); }
        catch (FormatException error) { throw new ArgumentException("Invalid SHA256 host key pin.", nameof(hostKeySha256), error); }
        if (expected.Length != 32 || Convert.ToBase64String(expected).TrimEnd('=') != pin)
            throw new ArgumentException("Invalid SHA256 host key pin.", nameof(hostKeySha256));
        return expected;
    }

    internal static Task<SshNetConnection> ConnectAsync(SshNetHostOptions options, CancellationToken ct)
    {
        var expected = Validate(options);
        return ConnectCommandAsync(options.Host, options.Port, options.Username, options.PrivateKeyPath,
            options.ConnectionTimeout, expected,
            SshCommand.BridgeCommand(options.RemoteExecutable, options.Endpoint), ct);
    }

    internal static Task<SshNetConnection> LaunchAsync(SshNetLaunchOptions options, CancellationToken ct)
    {
        var expected = Validate(options);
        return ConnectCommandAsync(options.Host, options.Port, options.Username, options.PrivateKeyPath,
            options.ConnectionTimeout, expected,
            SshCommand.StdioHostCommand(options.RemoteExecutable), ct);
    }

    private static async Task<SshNetConnection> ConnectCommandAsync(string host, int port, string username,
        string privateKeyPath, TimeSpan connectionTimeout, byte[] expected, string remoteCommand,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // SSH.NET does not implicitly use ssh-agent, ~/.ssh/config or known_hosts.
        // Host authentication is deliberately fail-closed by the supplied key pin.
        var key = new PrivateKeyFile(privateKeyPath);
        SshClient? client = null;
        SshNetCommand? command = null;
        Stream? stdin = null;
        try
        {
            var connection = new PrivateKeyConnectionInfo(host, port, username, key)
            {
                Timeout = connectionTimeout
            };
            client = new SshClient(connection);
            client.HostKeyReceived += (_, args) =>
            {
                var actual = SHA256.HashData(args.HostKey);
                args.CanTrust = CryptographicOperations.FixedTimeEquals(actual, expected);
            };
            await client.ConnectAsync(ct);
            ct.ThrowIfCancellationRequested();
            command = client.CreateCommand(remoteCommand);
            // Do not use RunCommand/Result: they buffer stdout and destroy RPC framing.
            var executing = command.ExecuteAsync(CancellationToken.None);
            stdin = command.CreateInputStream();
            return new(client, key, command, executing, stdin);
        }
        catch
        {
            stdin?.Dispose();
            command?.Dispose();
            client?.Dispose();
            key.Dispose();
            throw;
        }
    }

    private async Task DrainStderrAsync()
    {
        var buffer = new byte[2048];
        var chars = new char[2048];
        var decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                var n = await command.ExtendedOutputStream.ReadAsync(buffer);
                if (n == 0) return;
                var written = decoder.GetChars(buffer.AsSpan(0, n), chars, flush: false);
                diagnostics.Append(chars.AsSpan(0, written));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or
                                      Renci.SshNet.Common.SshException)
        {
            // Teardown closes channel streams while diagnostics may still be reading.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        // Closing a persistent bridge does not cancel Host work. An ephemeral
        // stdio Host instead observes EOF and cancels its private coordinator.
        try { input.Dispose(); }
        finally
        {
            try { client.Dispose(); }
            finally
            {
                command.Dispose();
                key.Dispose();
            }
        }
        try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { /* Never wait unboundedly for remote stderr EOF. */ }
    }
}
