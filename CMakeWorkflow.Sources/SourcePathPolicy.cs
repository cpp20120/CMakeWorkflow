using System.Text;

namespace CMakeWorkflow.Sources;

public static class SourcePathPolicy
{
    private static readonly HashSet<string> WindowsDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static void Validate(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (string.IsNullOrWhiteSpace(path) || !path.IsNormalized(NormalizationForm.FormC) ||
            path.StartsWith('/') ||
            path.Contains('\\', StringComparison.Ordinal) || path.Contains(':', StringComparison.Ordinal) ||
            path.Contains('\0', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Unsafe or noncanonical source path: {path}");
        }

        foreach (string segment in path.Split('/'))
        {
            string deviceStem = segment.Split('.')[0];
            if (segment.Length == 0 || segment is "." or ".." ||
                segment.EndsWith('.') || segment.EndsWith(' ') ||
                segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                WindowsDevices.Contains(deviceStem))
            {
                throw new InvalidDataException($"Unsafe source path: {path}");
            }
        }
    }
}
