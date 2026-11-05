using System.Text;

#pragma warning disable IDE0130 // Preserve the established namespace; folders only group implementation.
namespace CMakeWorkflow.Client;
#pragma warning restore IDE0130

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
