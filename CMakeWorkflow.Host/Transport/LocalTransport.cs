using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Host;
#pragma warning restore IDE0130

public static class LocalTransport
{
    private const string PipePrefix = @"\\.\pipe\cmakeworkflow-";

    public static string PipeName(string endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        return !endpoint.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase)
            || endpoint[PipePrefix.Length..].Length is < 1 or > 100
            || endpoint[PipePrefix.Length..].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')
            ? throw new ArgumentException(@"Use \\.\pipe\cmakeworkflow-NAME with an alphanumeric NAME.")
            : endpoint[@"\\.\pipe\".Length..];
    }

    public static void ValidateUnixEndpoint(string endpoint)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if (!Path.IsPathFullyQualified(endpoint) || Encoding.UTF8.GetByteCount(endpoint) > 100)
        {
            throw new ArgumentException("Use an absolute Unix socket path of at most 100 UTF-8 bytes.");
        }

        string directory = Path.GetDirectoryName(endpoint)!;
        // Caller creates a dedicated private directory. Never chmod a user's
        // existing directory, and never unlink a stale or unrelated socket.
        if (!Directory.Exists(directory) || new DirectoryInfo(directory).LinkTarget is not null
                                         || (File.GetUnixFileMode(directory) & (UnixFileMode.GroupRead |
                                             UnixFileMode.GroupWrite
                                             | UnixFileMode.GroupExecute | UnixFileMode.OtherRead |
                                             UnixFileMode.OtherWrite
                                             | UnixFileMode.OtherExecute)) != 0)
        {
            throw new ArgumentException(
                "Unix socket parent must be an existing private directory (mode 0700), not a symlink.");
        }
    }

    public static async Task<Stream> ConnectAsync(string endpoint, CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            NamedPipeClientStream pipe = new(".", PipeName(endpoint), PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            try
            {
                await pipe.ConnectAsync(ct);
                return pipe;
            }
            catch
            {
                await pipe.DisposeAsync();
                throw;
            }
        }

        ValidateUnixEndpoint(endpoint);

#pragma warning disable CA2000 // Ownership transfers to the returned object or connection task; surrounding using/catch/finally paths release failures.
        Socket socket = new(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
#pragma warning restore CA2000

        try
        {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(endpoint), ct);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}