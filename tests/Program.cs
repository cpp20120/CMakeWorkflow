using CMakeWorkflow.IntegrationTests.Execution;
using CMakeWorkflow.IntegrationTests.Protocol;
using CMakeWorkflow.IntegrationTests.Recipes;
using CMakeWorkflow.IntegrationTests.Support;

if (args.Length is < 1 or > 2 || !File.Exists(args[0]) || (args.Length == 2 && args[1] is not ("--recovery" or "--remote-cli" or "--workflow" or "--process-boundaries" or "--artifacts")))
{
    Console.Error.WriteLine(
        "Usage: dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -- /path/to/CMakeWorkflow [--recovery|--remote-cli|--workflow|--process-boundaries]");
    return 2;
}

TestHarness.Cli = Path.GetFullPath(args[0]);
string directory = Directory.GetCurrentDirectory();
while (!File.Exists(Path.Combine(directory, "CMakeWorkflow.csproj")))
{
    directory = Directory.GetParent(directory)?.FullName ??
                throw new System.Diagnostics.UnreachableException("Run tests inside the standalone project tree.");
}

TestHarness.Root = directory;
string? previousHistory = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB");
string historyDatabase = Path.Combine(Path.GetTempPath(),
    "cmake-workflow-integration-history-" + Guid.NewGuid().ToString("N") + ".sqlite");
Environment.SetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB", historyDatabase);
try
{
    if (args.Length == 2 && args[1] == "--process-boundaries")
    {
        BoundaryTests.Run();
        LifecycleTests.Run();
        Console.WriteLine("All workflow process boundary checks passed.");
        return 0;
    }
    if (args.Length == 2 && args[1] == "--workflow")
    {
        WorkflowTests.Run();
        Console.WriteLine("All workflow integration checks passed.");
        return 0;
    }
    if (args.Length == 2 && args[1] == "--artifacts")
    {
        ArtifactTests.Run();
        Console.WriteLine("All workflow artifact checks passed.");
        return 0;
    }
    if (args.Length == 2 && args[1] == "--remote-cli")
    {
        RemoteCliTests.Run();
        Console.WriteLine("Remote CLI argument validation passed.");
        return 0;
    }
    if (args.Length == 2)
    {
        RecoveryTests.Run();
        Console.WriteLine("All recovery fault-injection checks passed.");
        return 0;
    }

    RemoteCliTests.Run();
    ReportTests.Run();
    BenchmarkRecipeTests.Run();
    TypedRecipeIntegrationTests.Run();
    ProtocolTests.Run();
    WorkflowTests.Run();
    ArtifactTests.Run();
    BoundaryTests.Run();
    LifecycleTests.Run();
    Console.WriteLine("All integration checks passed.");
    return 0;
}

#pragma warning disable CA1031 // This failure boundary records or aggregates the primary and cleanup failures before returning a domain result.
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
#pragma warning restore CA1031

finally
{
    Environment.SetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB", previousHistory);
    if (File.Exists(historyDatabase))
    {
        File.Delete(historyDatabase);
    }
}
