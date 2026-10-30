using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Host;

internal sealed record RecoveryRunBinding(string Directory, string Snapshot)
{
    public static async Task<RecoveryRunBinding> ReadAsync(string workspace, string runDirectory,
        string[] profiles, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(runDirectory))
            throw new ArgumentException("Run directory must be absolute.");
        var root = Path.GetFullPath(workspace);
        var directory = Path.GetFullPath(runDirectory);
        var relative = Path.GetRelativePath(root, directory);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathFullyQualified(relative))
            throw new ArgumentException("Run directory must belong to the initialized workspace.");
        // Run directories and their parents must not redirect outside workspace.
        for (var path = directory; !string.Equals(path, root, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal); path = Path.GetDirectoryName(path)!)
        {
             if (path is null || !System.IO.Directory.Exists(path)) throw new ArgumentException("Run directory does not exist.");            
             if (new DirectoryInfo(path).LinkTarget is not null)
                throw new ArgumentException("Symlinked run directories are not supported.");
        }
        var file = Path.Combine(directory, "run.json");
        if (new FileInfo(file).LinkTarget is not null)
            throw new ArgumentException("Symlinked run report is not supported.");
        if (new FileInfo(file).Length > 16 * 1024 * 1024)
            throw new ArgumentException("Run report exceeds the recovery size limit.");
        var bytes = await File.ReadAllBytesAsync(file, ct);
        using var json = JsonDocument.Parse(bytes);
        var rootElement = json.RootElement;
        if (!rootElement.TryGetProperty("status", out var status))
            rootElement.TryGetProperty("Status", out status);
        if (status.ValueKind != JsonValueKind.String || status.GetString() != "needs_attention")
            throw new ArgumentException("Only needs_attention runs can be fixed.");
        if (!rootElement.TryGetProperty("findings", out var findings))
            rootElement.TryGetProperty("Findings", out findings);
        if (findings.ValueKind != JsonValueKind.Array || findings.GetArrayLength() == 0)
            throw new ArgumentException("Suspended run contains no findings.");
        var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var finding in findings.EnumerateArray())
        {
            if (!finding.TryGetProperty("suggestedProfiles", out var proposed))
                finding.TryGetProperty("SuggestedProfiles", out proposed);
            if (proposed.ValueKind != JsonValueKind.Array) continue;
            foreach (var profile in proposed.EnumerateArray())
                if (profile.ValueKind == JsonValueKind.String && profile.GetString() is { } id)
                    suggestions.Add(id);
        }
        if (profiles.Length == 0 || profiles.Any(profile => !suggestions.Contains(profile)))
            throw new ArgumentException("Fix profiles must be suggested by the run findings.");
        return new(directory, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    public static async Task<bool> MatchesAsync(string workspace, string directory, string[] profiles,
        string snapshot, CancellationToken ct)
    {
        try { return (await ReadAsync(workspace, directory, profiles, ct)).Snapshot == snapshot; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException
            or JsonException) { return false; }
    }
}
