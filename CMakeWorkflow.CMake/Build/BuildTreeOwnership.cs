using System.Security.Cryptography;
using System.Text;
using System.Diagnostics;
using System.Text.Json;
using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.CMake.Recipes;

namespace CMakeWorkflow.CMake.Build;

public sealed class BuildTreeOwnership : IDisposable
{
    private readonly List<FileStream> leases = [];
    private readonly List<string> paths = [];
    private static readonly HashSet<string> owned = new(WorkflowPlanner.PathComparer);

    public static string CanonicalPath(string path) =>
        TwoTargetWorkflow.CanonicalExistingDirectory(path, allowMissing: true);

    public static void RejectController(string tree, string? controller)
    {
        controller ??= Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_CONTROLLER_BUILD_DIR");
        if (controller is not null &&
            WorkflowPlanner.PathComparer.Equals(CanonicalPath(tree), CanonicalPath(controller)))
        {
            throw new InvalidOperationException(
                "Unsafe nested build: worker and controller build directories are identical. Launch externally or use an independent worker tree.");
        }

        if (Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES") is { } inherited)
        {
            using JsonDocument json = JsonDocument.Parse(inherited);
            foreach (JsonElement parentTree in json.RootElement.EnumerateArray())
            {
                if (WorkflowPlanner.PathComparer.Equals(CanonicalPath(tree), CanonicalPath(parentTree.GetString()!)))
                {
                    throw new InvalidOperationException(
                        "Unsafe nested build: this tree is already owned by the parent CMakeWorkflow invocation.");
                }
            }
        }
    }

    public static void PropagateTo(ProcessStartInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        List<string> inherited = [];
        if (Environment.GetEnvironmentVariable("CMAKE_WORKFLOW_OWNED_TREES") is { } value)
        {
            using JsonDocument json = JsonDocument.Parse(value);
            inherited.AddRange(json.RootElement.EnumerateArray().Select(p => p.GetString()!));
        }

        lock (owned)
        {
            info.Environment["CMAKE_WORKFLOW_OWNED_TREES"] = JsonSerializer.Serialize(
                [.. inherited.Concat(owned).Distinct(WorkflowPlanner.PathComparer)],
                Serialization.WorkflowJson.Default.ListString);
        }
    }

    public static async Task<BuildTreeOwnership> AcquireAsync(IEnumerable<string> trees, CancellationToken ct)
    {
        BuildTreeOwnership owner = new();
        try
        {
            // The same protocol is used by workflow, target and sequence commands.
            // Store locks outside build trees so an unconfigured context is not mutated.
            string lockRoot = Path.Combine(Path.GetTempPath(), "cmake-workflow-build-locks");
            _ = Directory.CreateDirectory(lockRoot);
            foreach (string? tree in trees.Select(CanonicalPath).Distinct(WorkflowPlanner.PathComparer)
                         .Order(WorkflowPlanner.PathComparer))
            {
                RejectController(tree, null);
                string key = OperatingSystem.IsWindows() ? tree.ToUpperInvariant() : tree;
                string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))) + ".lock";
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        owner.leases.Add(new FileStream(Path.Combine(lockRoot, name), FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None));
                        owner.paths.Add(tree);
                        lock (owned)
                        {
                            _ = owned.Add(tree);
                        }

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
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        foreach (FileStream lease in leases)
        {
            lease.Dispose();
        }

        lock (owned)
        {
            foreach (string path in paths)
            {
                _ = owned.Remove(path);
            }
        }

        paths.Clear();
        leases.Clear();
    }
}