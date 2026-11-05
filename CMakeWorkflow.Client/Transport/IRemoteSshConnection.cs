#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

// One full-duplex JSON-RPC byte transport, either OpenSSH or SSH.NET.
// Persistent Host operations survive channel close; private Host operations do not.
internal interface IRemoteSshConnection : IAsyncDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    string Diagnostics { get; }
    int? ExitCode { get; }
}
