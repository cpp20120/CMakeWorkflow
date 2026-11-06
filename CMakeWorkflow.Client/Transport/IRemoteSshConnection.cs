namespace CMakeWorkflow.Client.Transport;

// One full-duplex JSON-RPC byte transport, either OpenSSH or SSH.NET.
// Persistent Host operations survive channel close; private Host operations do not.
/// <summary>Owns one duplex SSH JSON-RPC channel and its teardown resources.</summary>
/// <remarks>
/// Input receives remote stdout; Output sends remote stdin. Dispose must terminate/drain transport resources
/// before pending RPC handler I/O is disposed. Diagnostic stderr is separate from protocol bytes.
/// Channel close leaves connected persistent Host execution alive; private stdio Hosts stop with the channel.
/// </remarks>
internal interface IRemoteSshConnection : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    string Diagnostics { get; }
    int? ExitCode { get; }
}
