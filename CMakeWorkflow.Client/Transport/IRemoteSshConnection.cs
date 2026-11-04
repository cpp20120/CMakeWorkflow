namespace CMakeWorkflow.Client;

// One full-duplex JSON-RPC byte transport, either OpenSSH or SSH.NET.
// Closing this channel never cancels a durable workflow accepted by the Host.
internal interface IRemoteSshConnection : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    string Diagnostics { get; }
    int? ExitCode { get; }
}
