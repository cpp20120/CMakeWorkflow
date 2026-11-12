using System.Security.Cryptography;
using System.Text;
using Renci.SshNet;
using SshNetCommand = Renci.SshNet.SshCommand;

namespace CMakeWorkflow.Client.Transport;

/// <summary>Owns an SSH.NET client, key and binary exec channel for one RPC connection.</summary>
/// <remarks>
/// No PTY or stdout text buffering is used; stderr is drained separately into bounded diagnostics.
/// Callers borrow Input/Output until disposal and serialize disposal; the disposed flag is not a concurrency lock.
/// Exec starts independently of the connect token. Channel lifetime belongs to this object; persistent bridge
/// shutdown leaves Host operations alive, while private stdio Host shutdown revokes its coordinator lifetime.
/// </remarks>
internal sealed class SshNetConnection : IRemoteSshConnection
{
    private readonly SshClient client;
    private readonly PrivateKeyFile key;
    private readonly SshNetCommand command;
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
        Output = input;
        stderr = DrainStderrAsync();
        // Observe early channel failures even if the RPC handshake fails first.
        _ = execution.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    public Stream Input => command.OutputStream;
    public Stream Output { get; }
    public string Diagnostics => diagnostics.Snapshot();
    public int? ExitCode => execution.IsCompleted ? command.ExitStatus : null;

    /// <summary>Validates SSH settings and decodes a canonical SHA-256 host-key pin without opening resources.</summary>
    /// <remarks>
    /// Rejects invalid pin/path/port/deadline settings before reading a private key or opening a socket.
    /// Remote Linux paths are validated as remote strings and never resolved on the client filesystem.
    /// </remarks>
    /// <param name="options">Persistent bridge settings.</param>
    /// <returns>New 32-byte expected host-key digest.</returns>
    internal static byte[] Validate(SshNetHostOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        SshCommand.RemotePath(options.Endpoint, nameof(options.Endpoint));
        return ValidateCommon(options.Host, options.Username, options.PrivateKeyPath,
            options.HostKeySha256, options.Workspace, options.RemoteExecutable,
            options.Port, options.ConnectionTimeout);
    }

    /// <summary>Validates SSH settings and decodes a canonical SHA-256 host-key pin without opening resources.</summary>
    /// <remarks>
    /// Rejects invalid pin/path/port/deadline settings before reading a private key or opening a socket.
    /// Remote Linux paths are validated as remote strings and never resolved on the client filesystem.
    /// </remarks>
    /// <param name="options">Private stdio Host settings.</param>
    /// <returns>New 32-byte expected host-key digest.</returns>
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
        foreach ((string? value, string? name) in new[] {
            (host, nameof(host)), (username, nameof(username)),
            (privateKeyPath, nameof(privateKeyPath)), (remoteExecutable, nameof(remoteExecutable)) })
        {
            if (string.IsNullOrWhiteSpace(value) || value.Any(char.IsControl))
            {
                throw new ArgumentException("Value must be nonempty and contain no control characters.", name);
            }
        }

        if (host.Any(char.IsWhiteSpace) || username.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Host and username cannot contain whitespace.");
        }

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port));
        }

        if (!Path.IsPathFullyQualified(privateKeyPath))
        {
            throw new ArgumentException("Private key path must be absolute on the local machine.");
        }

        SshCommand.RemotePath(workspace, nameof(workspace));
        if (connectionTimeout <= TimeSpan.Zero || connectionTimeout > TimeSpan.FromHours(1))
        {
            throw new ArgumentOutOfRangeException(nameof(connectionTimeout));
        }

        string? pin = hostKeySha256 ?? throw new ArgumentException("A SHA256 host key pin is required.");
        if (pin.StartsWith("SHA256:", StringComparison.Ordinal))
        {
            pin = pin[7..];
        }

        if (pin.Length != 43 || !pin.All(c => char.IsLetterOrDigit(c) || c is '+' or '/'))
        {
            throw new ArgumentException("Host key pin must be OpenSSH SHA256:base64 (43 unpadded characters).");
        }

        byte[] expected;
        try { expected = Convert.FromBase64String(pin + "="); }
        catch (FormatException error) { throw new ArgumentException("Invalid SHA256 host key pin.", nameof(hostKeySha256), error); }
        return expected.Length != 32 || Convert.ToBase64String(expected).TrimEnd('=') != pin
            ? throw new ArgumentException("Invalid SHA256 host key pin.", nameof(hostKeySha256))
            : expected;
    }

    /// <summary>Opens one pinned-key channel running a bridge to an existing persistent Host.</summary>
    /// <remarks>
    /// Does not launch the daemon or initialize RPC. Returned transport owns SSH resources; it does not own
    /// remote accepted work. The failure path cleans up pre-transfer allocations; cleanup errors can propagate. No retry occurs.
    /// </remarks>
    internal static Task<SshNetConnection> ConnectAsync(SshNetHostOptions options, CancellationToken ct)
    {
        byte[] expected = Validate(options);
        return ConnectCommandAsync(options.Host, options.Port, options.Username, options.PrivateKeyPath,
            options.ConnectionTimeout, expected,
            SshCommand.BridgeCommand(options.RemoteExecutable, options.Endpoint), ct);
    }

    /// <summary>Opens one pinned-key channel running a private stdio Host.</summary>
    /// <remarks>
    /// Channel close ends the private Host lifetime. This only creates transport; the caller performs RPC
    /// initialization/readiness and owns teardown on handshake failure. No automatic reconnect or operation replay occurs.
    /// </remarks>
    internal static Task<SshNetConnection> LaunchAsync(SshNetLaunchOptions options, CancellationToken ct)
    {
        byte[] expected = Validate(options);
        return ConnectCommandAsync(options.Host, options.Port, options.Username, options.PrivateKeyPath,
            options.ConnectionTimeout, expected,
            SshCommand.StdioHostCommand(options.RemoteExecutable), ct);
    }

    /// <summary>Authenticates against the supplied key pin and transfers an unbuffered exec channel to its owner.</summary>
    /// <remarks>
    /// Uses only the supplied private key and pin, not ssh-agent, ssh config or known_hosts. The connect token
    /// is checked during connection and before exec; execution itself uses CancellationToken.None and must be
    /// closed by transport disposal. Construction failure attempts disposal of stdin, command, client and key independently, preserving the original failure.
    /// </remarks>
    private static async Task<SshNetConnection> ConnectCommandAsync(string host, int port, string username,
        string privateKeyPath, TimeSpan connectionTimeout, byte[] expected, string remoteCommand,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // SSH.NET does not implicitly use ssh-agent, ~/.ssh/config or known_hosts.
        // Host authentication is deliberately fail-closed by the supplied key pin.
        PrivateKeyFile key = new(privateKeyPath);
        SshClient? client = null;
        SshNetCommand? command = null;
        Stream? stdin = null;
        try
        {
            // The host/port/key overload owns its ConnectionInfo; the overload
            // accepting an existing ConnectionInfo does not.
            client = new SshClient(host, port, username, key);
            client.ConnectionInfo.Timeout = connectionTimeout;
            client.HostKeyReceived += (_, args) =>
            {
                byte[] actual = SHA256.HashData(args.HostKey);
                args.CanTrust = CryptographicOperations.FixedTimeEquals(actual, expected);
            };
            await client.ConnectAsync(ct);
            ct.ThrowIfCancellationRequested();
            command = client.CreateCommand(remoteCommand);
            // Do not use RunCommand/Result: they buffer stdout and destroy RPC framing.
            Task executing = command.ExecuteAsync(CancellationToken.None);
            stdin = command.CreateInputStream();
            return new(client, key, command, executing, stdin);
        }
        catch (Exception failure)
        {
            CleanupFailures cleanup = new();
            cleanup.Add(failure);
            await cleanup.DisposeAsync(stdin);
            cleanup.Dispose(command);
#pragma warning disable CA1508 // Construction can fail before client assignment; release only initialized resources.
            cleanup.Dispose(client);
#pragma warning restore CA1508
            cleanup.Dispose(key);
            cleanup.ThrowIfAny("SSH.NET connection and cleanup reported multiple failures.");
            throw; // ThrowIfAny always throws because the primary error was added first.
        }
    }

    private async Task DrainStderrAsync()
    {
        byte[] buffer = new byte[2048];
        char[] chars = new char[2048];
        Decoder decoder = Encoding.UTF8.GetDecoder();
        try
        {
            while (true)
            {
                int n = await command.ExtendedOutputStream.ReadAsync(buffer);
                if (n == 0)
                {
                    return;
                }

                int written = decoder.GetChars(buffer.AsSpan(0, n), chars, flush: false);
                diagnostics.Append(chars.AsSpan(0, written));
            }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException or
                                      Renci.SshNet.Common.SshException)
        {
            // Teardown closes channel streams while diagnostics may still be reading.
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Sequentially repeatable; concurrent calls are not synchronized. Attempts stdin/client/command/key
    /// disposal independently rather than sending durable cancellation or awaiting workflow completion.
    /// Waits at most two seconds for stderr after teardown; this is not a deadline covering every disposal
    /// call or remote Host cleanup. An early exec failure is observed by its continuation.
    /// </remarks>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        // Closing a persistent bridge does not cancel Host work. An ephemeral
        // stdio Host instead observes EOF and cancels its private coordinator.
        CleanupFailures cleanup = new();
        cleanup.Dispose(Output);
        // Direct field disposal is intentional: CA2213 cannot infer ownership
        // release through CleanupFailures.Dispose(IDisposable).
#pragma warning disable CA1031 // Continue releasing all SSH.NET resources after any Dispose failure.
        try
        {
            client.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cleanup.Add(error);
        }

        try
        {
            command.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cleanup.Add(error);
        }

        try
        {
            key.Dispose();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            cleanup.Add(error);
        }
#pragma warning restore CA1031

        try
        {
            await stderr.WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (TimeoutException) { /* Never wait unboundedly for remote stderr EOF. */ }
#pragma warning disable CA1031 // Observe stderr errors without masking earlier disposal failures.
        catch (Exception error) when (error is not OutOfMemoryException)
#pragma warning restore CA1031
        {
            cleanup.Add(error);
        }

        cleanup.ThrowIfAny("SSH.NET teardown reported multiple failures.");
    }
}
