namespace CMakeWorkflow.IntegrationTests;

internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length != 1 || !File.Exists(args[0]))
        {
            Console.Error.WriteLine("Usage: dotnet run --project tests/CMakeWorkflow.IntegrationTests.csproj -- /path/to/CMakeWorkflow");
            return 2;
        }
        TestHarness.Cli = Path.GetFullPath(args[0]);
        var directory = Directory.GetCurrentDirectory();
        while (!File.Exists(Path.Combine(directory, "CMakeWorkflow.csproj")))
            directory = Directory.GetParent(directory)?.FullName ?? throw new Exception("Run tests inside the standalone project tree.");
        TestHarness.Root = directory;
        var previousHistory = Environment.GetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB");
        var historyDatabase = Path.Combine(Path.GetTempPath(),
            "cmake-workflow-integration-history-" + Guid.NewGuid().ToString("N") + ".sqlite");
        Environment.SetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB", historyDatabase);
        try
        {
            ReportTests.Run();
            ProtocolTests.Run();
            WorkflowTests.Run();
            BoundaryTests.Run();
            LifecycleTests.Run();
            Console.WriteLine("All integration checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally
        {
            Environment.SetEnvironmentVariable("CMAKEWORKFLOW_HISTORY_DB", previousHistory);
            if (File.Exists(historyDatabase)) File.Delete(historyDatabase);
        }
    }
}
