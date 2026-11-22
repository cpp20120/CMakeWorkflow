using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using CMakeWorkflow.Client.Experiments;

namespace CMakeWorkflow.Client.AotSmoke;

internal static class ExperimentPlannerSmoke
{
    internal static async Task RunAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-sdk-plan-");
        try
        {
            string root = Directory.CreateDirectory(Path.Combine(scratch.FullName, "git")).FullName;
            string store = Path.Combine(scratch.FullName, "plans");
            await File.WriteAllTextAsync(Path.Combine(root, "CMakeLists.txt"),
                "cmake_minimum_required(VERSION 3.26)\nproject(sdk_bundle)\n", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "vcpkg.json"),
                """{"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["cxxopts"]}""", ct);
            await File.WriteAllTextAsync(Path.Combine(root, "probe.sh"), "#!/bin/sh\nexit 0\n", ct);
            await GitAsync(root, ["init", "-b", "main", "--template="], ct);
            await GitAsync(root, ["add", "."], ct);
            await GitAsync(root, ["update-index", "--chmod=+x", "probe.sh"], ct);
            await CommitAsync(root, ct);

            ExperimentPlanOptions options = new(new Uri(root).AbsoluteUri, "main",
                new ExperimentHost("example", "/home/cmwf/workspace", "/home/cmwf/host.sock"))
            {
                Configuration = "Release",
                Vcpkg = true,
                BenchmarkTarget = "benchmarks",
                Repeats = 2,
                BenchmarkArguments = ["argument with spaces"],
                Profile = true
            };
            PreparedExperimentPlan plan = await ExperimentPlanner.CreateAsync(options, store, ct);
            PreparedExperimentPlan repeated = await ExperimentPlanner.CreateAsync(options, store, ct);
            PreparedExperimentPlan loaded = await ExperimentService.ReadPlanAsync(plan.PlanPath, plan.Id, ct);
            using JsonDocument manifest = JsonDocument.Parse(plan.Manifest);
            if (plan.Id != repeated.Id || loaded.Id != plan.Id || loaded.Host != plan.Host ||
                plan.RequestedRef != "main" || plan.DependencyPlanSha256 is not { Length: 64 } ||
                plan.SourceFileCount != 3 || manifest.RootElement.GetProperty("nodes").GetArrayLength() != 7)
            {
                throw new InvalidDataException("SDK planner lost immutable experiment inputs.");
            }

            await File.WriteAllTextAsync(Path.Combine(root, "probe.sh"), "#!/bin/sh\nexit 1\n", ct);
            await GitAsync(root, ["add", "-u"], ct);
            await GitAsync(root, ["update-index", "--chmod=+x", "probe.sh"], ct);
            await CommitAsync(root, ct);
            PreparedExperimentPlan moved = await ExperimentPlanner.CreateAsync(options, store, ct);
            if (moved.Commit == plan.Commit || moved.SourceSnapshotId == plan.SourceSnapshotId ||
                moved.DependencyPlanSha256 == plan.DependencyPlanSha256)
            {
                throw new InvalidDataException("SDK planner ignored the new Git source identity.");
            }

            await using (ZipArchive zip = await ZipFile.OpenReadAsync(plan.ArchivePath, ct))
            {
                ZipArchiveEntry script = zip.GetEntry("probe.sh")!;
                using StreamReader reader = new(await script.OpenAsync(ct));
                if (script.ExternalAttributes != unchecked((int)0x81ED0000) ||
                    await reader.ReadToEndAsync(ct) != "#!/bin/sh\nexit 0\n")
                {
                    throw new InvalidDataException("The approved ZIP lost committed bytes or executable mode.");
                }
            }

            await File.AppendAllTextAsync(plan.PlanPath, " ", ct);
            try
            {
                _ = await ExperimentService.ReadPlanAsync(plan.PlanPath, plan.Id, ct);
                throw new InvalidOperationException("SDK reader accepted a changed plan.");
            }
            catch (InvalidDataException)
            {
            }
            Console.WriteLine("PASS NativeAOT SDK experiment planner, vcpkg identity and pinned ZIP.");
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static Task CommitAsync(string root, CancellationToken ct) =>
        GitAsync(root, ["-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid", "commit", "-m", "fixture"], ct);

    private static async Task GitAsync(string root, string[] arguments, CancellationToken ct)
    {
        ProcessStartInfo start = new("git") { WorkingDirectory = root, UseShellExecute = false };
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("core.hooksPath=/dev/null");
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }
        using Process process = Process.Start(start) ?? throw new IOException("Cannot start Git fixture.");
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            throw new IOException("Cannot create Git fixture.");
        }
    }
}
