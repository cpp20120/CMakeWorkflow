namespace CMakeWorkflow.Client.Transport;

// One full-duplex JSON-RPC byte transport, either OpenSSH or SSH.NET.
// Persistent Host operations survive channel close; private Host operations do not.
internal interface IRemoteSshConnection : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    string Diagnostics { get; }
    int? ExitCode { get; }
}
