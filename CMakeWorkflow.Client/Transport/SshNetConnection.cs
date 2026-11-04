using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using SshNetCommand = Renci.SshNet.SshCommand;

namespace CMakeWorkflow.Client;

/// <summary>
/// SSH exec channel, without PTY: raw binary stdin/stdout for JSON-RPC, stderr
/// exclusively for bounded diagnostics. It owns the SSH channel, not the Host.
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
        foreach (var (text, name) in new[] {
            (options.Host, nameof(options.Host)), (options.Username, nameof(options.Username)),
            (options.PrivateKeyPath, nameof(options.PrivateKeyPath)),
            (options.RemoteExecutable, nameof(options.RemoteExecutable)) })
            if (string.IsNullOrWhiteSpace(text) || text.Any(char.IsControl))
                throw new ArgumentException("Value must be nonempty and contain no control characters.", name);
        if (options.Host.Any(char.IsWhiteSpace) || options.Username.Any(char.IsWhiteSpace))
            throw new ArgumentException("Host and username cannot contain whitespace.");
        if (options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options.Port));
        if (!Path.IsPathFullyQualified(options.PrivateKeyPath))
            throw new ArgumentException("Private key path must be absolute on the local machine.");
        SshCommand.RemotePath(options.Endpoint, nameof(options.Endpoint));
        SshCommand.RemotePath(options.Workspace, nameof(options.Workspace));
        if (options.ConnectionTimeout <= TimeSpan.Zero || options.ConnectionTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(options.ConnectionTimeout));

        var pin = options.HostKeySha256;
        if (pin is null) throw new ArgumentException("A SHA256 host key pin is required.");
        if (pin.StartsWith("SHA256:", StringComparison.Ordinal)) pin = pin[7..];
        if (pin.Length != 43 || !pin.All(c => char.IsLetterOrDigit(c) || c is '+' or '/'))
            throw new ArgumentException("Host key pin must be OpenSSH SHA256:base64 (43 unpadded characters).");
        byte[] expected;
        try { expected = Convert.FromBase64String(pin + "="); }
        catch (FormatException error) { throw new ArgumentException("Invalid SHA256 host key pin.", nameof(options.HostKeySha256), error); }
        if (expected.Length != 32 || Convert.ToBase64String(expected).TrimEnd('=') != pin)
            throw new ArgumentException("Invalid SHA256 host key pin.", nameof(options.HostKeySha256));
        return expected;
    }

    internal static async Task<SshNetConnection> ConnectAsync(SshNetHostOptions options, CancellationToken ct)
    {
        var expected = Validate(options);
        ct.ThrowIfCancellationRequested();
        // SSH.NET does not implicitly use ssh-agent, ~/.ssh/config or known_hosts.
        // Host authentication is deliberately fail-closed by the supplied key pin.
        var key = new PrivateKeyFile(options.PrivateKeyPath);
        SshClient? client = null;
        SshNetCommand? command = null;
        Stream? stdin = null;
        try
        {
            var connection = new PrivateKeyConnectionInfo(options.Host, options.Port, options.Username, key)
            {
                Timeout = options.ConnectionTimeout
            };
            client = new SshClient(connection);
            client.HostKeyReceived += (_, args) =>
            {
                var actual = SHA256.HashData(args.HostKey);
                args.CanTrust = CryptographicOperations.FixedTimeEquals(actual, expected);
            };
            await client.ConnectAsync(ct);
            ct.ThrowIfCancellationRequested();
            command = client.CreateCommand(SshCommand.BridgeCommand(options.RemoteExecutable, options.Endpoint));
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
        // Shutdown is connection-local. Durable operations on the Host survive it.
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
