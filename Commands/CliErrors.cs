using System.ComponentModel;
using System.Text.Json;
using System.Xml;
using CMakeWorkflow.Client.Protocol;
using Microsoft.Data.Sqlite;
using Renci.SshNet.Common;
using StreamJsonRpc;

namespace CMakeWorkflow.Commands;

/// <summary>Identifies input, environment and transport failures that have a concise CLI diagnostic.</summary>
/// <remarks>Unrecognized exceptions propagate; an aggregate qualifies only when every inner failure qualifies.</remarks>
internal static class CliErrors
{
    internal static bool IsExpected(Exception error) => error switch
    {
        ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or NotSupportedException or TimeoutException or FormatException or OverflowException or
            KeyNotFoundException or JsonException or XmlException or Win32Exception or SqliteException or
            SshException or RemoteRpcException or ConnectionLostException or CMakeWorkflowRpcRefusalException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Count != 0 && aggregate.InnerExceptions.All(IsExpected),
        _ => false
    };
}
