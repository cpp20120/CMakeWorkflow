using System.Text;

namespace CMakeWorkflow.Client.Transport;

/// <summary>Thread-safe tail buffer retaining at most 16 Ki UTF-16 characters of transport diagnostics.</summary>
/// <remarks>
/// Append and Snapshot use the same lock; returned strings are detached snapshots. Truncation discards
/// oldest characters and can split a Unicode surrogate pair. Diagnostic text is never mixed into RPC framing.
/// </remarks>
internal sealed class BoundedSshDiagnostics
{
    private const int Limit = 16 * 1024;
    private readonly StringBuilder tail = new();
    private readonly Lock gate = new();

    internal void Append(ReadOnlySpan<char> text)
    {
        lock (gate)
        {
            if (text.Length > Limit)
            {
                text = text[^Limit..];
            }

            int excess = tail.Length + text.Length - Limit;
            if (excess > 0)
            {
                _ = tail.Remove(0, excess);
            }

            _ = tail.Append(text);
        }
    }

    internal string Snapshot()
    {
        lock (gate)
        {
            return tail.ToString();
        }
    }
}
