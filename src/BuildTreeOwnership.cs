using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow;

public sealed class BuildTreeOwnership : IDisposable
{
    private readonly List<FileStream> leases = [];
    private readonly List<string> paths = [];
    private static readonly HashSet<string> owned = new(WorkflowPlanner.PathComparer);

    public static string CanonicalPath(string path) => TwoTargetWorkflow.CanonicalExistingDirectory(path, allowMissing: true);

    public static void RejectController(string tree, string? controller)
    {
        controller ??= Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR");
        if (controller is not null && WorkflowPlanner.PathComparer.Equals(CanonicalPath(tree), CanonicalPath(controller)))
            throw new InvalidOperationException("Unsafe nested build: worker and controller build directories are identical. Launch externally or use an independent worker tree.");
        if (Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES") is { } inherited)
        {
            using var json = JsonDocument.Parse(inherited);
            foreach (var parentTree in json.RootElement.EnumerateArray())
                if (WorkflowPlanner.PathComparer.Equals(CanonicalPath(tree), CanonicalPath(parentTree.GetString()!)))
                    throw new InvalidOperationException("Unsafe nested build: this tree is already owned by the parent CMakeWorkflow invocation.");
        }
    }

    public static void PropagateTo(ProcessStartInfo info)
    {
        var inherited = new List<string>();
        if (Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES") is { } value)
        {
            using var json = JsonDocument.Parse(value);
            inherited.AddRange(json.RootElement.EnumerateArray().Select(p => p.GetString()!));
        }
        lock (owned)
            info.Environment["CMAKE_WORKFLOW_OWNED_TREES"] = JsonSerializer.Serialize(
                inherited.Concat(owned).Distinct(WorkflowPlanner.PathComparer).ToList(), WorkflowJson.Default.ListString);
    }

    public static async Task<BuildTreeOwnership> AcquireAsync(IEnumerable<string> trees, CancellationToken ct)
    {
        var owner = new BuildTreeOwnership();
        try
        {
            // The same protocol is used by workflow, target and sequence commands.
            // Store locks outside build trees so an unconfigured context is not mutated.
            var lockRoot = Path.Combine(Path.GetTempPath(), "cmake-workflow-build-locks");
            Directory.CreateDirectory(lockRoot);
            foreach (var tree in trees.Select(CanonicalPath).Distinct(WorkflowPlanner.PathComparer).Order(WorkflowPlanner.PathComparer))
            {
                RejectController(tree, null);
                var key = OperatingSystem.IsWindows() ? tree.ToUpperInvariant() : tree;
                var name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock";
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        owner.leases.Add(new FileStream(Path.Combine(lockRoot, name), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None));
                        owner.paths.Add(tree);
                        lock (owned) owned.Add(tree);
                        break;
                    }
                    // EAGAIN is 11 on Linux and 35 on macOS; Windows reports
                    // sharing/lock violations as 32/33 respectively.
                    catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
                    {
                        await Task.Delay(100, ct);
                    }
                }
            }
            return owner;
        }
        catch { owner.Dispose(); throw; }
    }

    public void Dispose()
    {
        foreach (var lease in leases) lease.Dispose();
        lock (owned)
            foreach (var path in paths) owned.Remove(path);
        paths.Clear();
        leases.Clear();
    }
}
