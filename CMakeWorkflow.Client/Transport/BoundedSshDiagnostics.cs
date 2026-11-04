using System.Text;

namespace CMakeWorkflow.Client;

internal sealed class BoundedSshDiagnostics
{
    private const int Limit = 16 * 1024;
    private readonly StringBuilder tail = new();
    private readonly object gate = new();

    internal void Append(ReadOnlySpan<char> text)
    {
        lock (gate)
        {
            if (text.Length > Limit) text = text[^Limit..];
            var excess = tail.Length + text.Length - Limit;
            if (excess > 0) tail.Remove(0, excess);
            tail.Append(text);
        }
    }

    internal string Snapshot()
    {
        lock (gate) return tail.ToString();
    }
}
