using System.Text.Json;
using CMakeWorkflow.CMake.Recovery;
using CMakeWorkflow.Core.Persistence;
using CMakeWorkflow.Storage.Documents;

namespace CMakeWorkflow.Commands;

internal static class ArtifactCasCommand
{
    private const string Usage = """
        Usage: CMakeWorkflow cas STORE COMMAND
          init-indexed
          import-actions LEGACY_STORE --key-policy 1|2
          show OPERATION
          fetch OPERATION ARTIFACT_ID OUTPUT
          pin OPERATION
          unpin OPERATION
          drop-action ACTION_DIGEST
          gc [--apply]
        Use remote stash --operation OPERATION --store STORE to import finalized artifacts.
        """;

    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is [] or ["--help"] or ["help"])
        {
            Console.WriteLine(Usage);
            return 0;
        }

        switch (args)
        {
            case [string root, "init-indexed"]:
                _ = await DocumentStore.InitializeIndexedActionsAsync(root, ct);
                break;
            case [string root, "import-actions", string source, "--key-policy", "1" or "2"]:
                int count = await new CasActions(new(root), args[4] == "1" ? 1 : 2)
                    .ImportLegacyAsync(new(source), () => RecoveryFaults.HitAsync("action-import-before-commit", null, ct), ct);
                Console.WriteLine(count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                break;
            case [string root, "show", string id]:
                Print(await new ArtifactCas(root).ShowAsync(id, ct));
                break;
            case [string root, "pin" or "unpin", string id]:
                Print(await new ArtifactCas(root).PinAsync(id, args[1] == "pin", ct));
                break;
            case [string root, "fetch", string id, string artifact, string output]:
                await new ArtifactCas(root).FetchAsync(id, artifact, output, ct);
                Console.WriteLine(Path.GetFullPath(output));
                break;
            case [string root, "drop-action", string action]:
                await new CasActions(new ArtifactStoreFiles(root)).DropAsync(action, ct);
                break;
            case [string, "gc"] or [string, "gc", "--apply"]:
                CasGcResult report = await new ArtifactCas(args[0]).CollectAsync(args.Length == 3, ct);
                Console.WriteLine(JsonSerializer.Serialize(report, ArtifactCasJson.Default.CasGcResult));
                break;
            default:
                throw new ArgumentException(Usage);
        }

        return 0;
    }

    internal static void Print(CasOperation receipt) =>
        Console.WriteLine(JsonSerializer.Serialize(receipt, ArtifactCasJson.Default.CasOperation));
}
