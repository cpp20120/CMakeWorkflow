using System.Diagnostics;
using System.Text.Json;

namespace CMakeWorkflow.Host.Tests.Durability;

internal static class ManagedProvisioningFaultTests
{
    internal static async Task RunAsync(string cli, string faultCli, string verifiedStore)
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromMinutes(12));
        CancellationToken ct = deadline.Token;
        DirectoryInfo scratch = Directory.CreateTempSubdirectory("cmwf-provision-fault-");
        try
        {
            string fresh = Path.Combine(scratch.FullName, "fresh");
            string plan = Path.Combine(scratch.FullName, "tools.capnp");
            string replay = Path.Combine(scratch.FullName, "review-again.capnp");
            string[] flags = ["--profile", "cmake-ninja", "--tool-policy", "ManagedOnly"];
            (string planResult, string approvalOutput) = await RunCommandAsync(cli, fresh,
                ["tooling", "plan", .. flags, "--binary-plan", plan, "--json"], ct);
            using (JsonDocument reviewed = JsonDocument.Parse(planResult))
            {
                Check(!reviewed.RootElement.GetProperty("ready").GetBoolean(), "Provisioning fault fixture is already installed.");
            }

            const string prefix = "Binary plan approval: ";
            string approval = approvalOutput.Split('\n').Single(line => line.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..].Trim();
            string[] apply = ["tooling", "setup", .. flags, "--binary-plan", plan, "--yes", "--plan-sha", approval, "--json"];
            await InterruptAsync(faultCli, fresh, apply, "before-tool-publication", scratch.FullName, ct);
            (string afterStop, _) = await RunCommandAsync(cli, fresh,
                ["tooling", "plan", .. flags, "--binary-plan", replay, "--json"], ct);
            using (JsonDocument pending = JsonDocument.Parse(afterStop))
            {
                Check(!pending.RootElement.GetProperty("ready").GetBoolean() &&
                    pending.RootElement.GetProperty("tools").EnumerateArray().All(tool => !tool.GetProperty("installed").GetBoolean()),
                    "Unpublished installation satisfied managed verification.");
            }
            byte[] expectedPlan = await File.ReadAllBytesAsync(plan, ct);
            byte[] actualPlan = await File.ReadAllBytesAsync(replay, ct);
            Check(expectedPlan.AsSpan().SequenceEqual(actualPlan),
                "Incomplete staging changed the reviewed plan.");
            (string installed, _) = await RunCommandAsync(cli, fresh, apply, ct);
            using (JsonDocument completed = JsonDocument.Parse(installed))
            {
                Check(completed.RootElement.GetProperty("success").GetBoolean(), "Approved tooling retry failed.");
            }

            string[] lockArgs = ["--binary", "--profile", "linux-clang", "--generator", "Ninja Multi-Config", "--json"];
            (string resolved, _) = await RunCommandAsync(cli, verifiedStore, ["tooling", "resolve", .. lockArgs], ct);
            using JsonDocument resolution = JsonDocument.Parse(resolved);
            string environment = resolution.RootElement.GetProperty("environmentSha256").GetString()!;
            string lockFile = Path.Combine(verifiedStore, "manifests", environment + ".capnp");
            Check(!File.Exists(lockFile), "The fault fixture requires an unpublished environment lock.");
            string[] publish = ["tooling", "lock", .. lockArgs, "--approve-sha", environment];
            await InterruptAsync(faultCli, verifiedStore, publish, "before-environment-publication", scratch.FullName, ct);
            Check(!File.Exists(lockFile), "Interrupted environment publication left a visible lock.");
            _ = await RunCommandAsync(cli, verifiedStore, publish, ct);
            _ = await RunCommandAsync(cli, verifiedStore, ["tooling", "verify", "--environment-lock", environment, "--json"], ct);

            string project = Directory.CreateDirectory(Path.Combine(scratch.FullName, "project")).FullName;
            await File.WriteAllTextAsync(Path.Combine(project, "vcpkg.json"), """
                {"builtin-baseline":"84bab45d415d22042bd0b9081aea57f362da3f35","dependencies":["cxxopts"]}
                """, ct);
            string dependencyPlan = Path.Combine(scratch.FullName, "deps.capnp");
            (string dependencyResult, _) = await RunCommandAsync(cli, verifiedStore,
                ["tooling", "vcpkg", "plan", project, "--binary-plan", dependencyPlan, "--environment-lock", environment], ct);
            string dependency = dependencyResult.Trim();
            string[] provision = ["tooling", "vcpkg", "install", project, "--yes", "--plan-sha", dependency,
                "--binary-plan", dependencyPlan, "--environment-lock", environment];
            string receipt = Path.Combine(verifiedStore, "vcpkg", "projects", dependency, "receipt.capnp");
            await InterruptAsync(faultCli, verifiedStore, provision, "before-dependency-receipt", scratch.FullName, ct);
            Check(!File.Exists(receipt), "Interrupted dependency install left a visible receipt.");
            _ = await RunCommandAsync(cli, verifiedStore, provision, ct);
            Check(File.Exists(receipt), "Approved dependency retry did not publish its receipt.");
            _ = await RunCommandAsync(cli, verifiedStore, provision, ct);
            Console.WriteLine("Real managed tool, environment lock and vcpkg receipt publication interrupts passed.");
        }
        finally
        {
            scratch.Delete(recursive: true);
        }
    }

    private static async Task InterruptAsync(string cli, string store, string[] args, string boundary, string scratch,
        CancellationToken ct)
    {
        string marker = Path.Combine(scratch, boundary);
        ProcessStartInfo info = Info(cli, store, args);
        info.Environment["CMAKEWORKFLOW_TEST_PROVISION_BOUNDARY"] = boundary;
        info.Environment["CMAKEWORKFLOW_TEST_PROVISION_MARKER"] = marker;
        using Process child = Process.Start(info) ?? throw new InvalidOperationException("Could not start provisioning.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = child.StandardError.ReadToEndAsync(ct);
        try
        {
            while (!File.Exists(marker) && !child.HasExited)
            {
                await Task.Delay(50, ct);
            }
            if (child.HasExited)
            {
                throw new InvalidOperationException("Provisioner exited before " + boundary + ": " + await stdout + await stderr);
            }

            child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync(ct);
            Check(await File.ReadAllTextAsync(marker, ct) == boundary && child.ExitCode != 0,
                "Process kill did not interrupt the expected boundary.");
        }
        finally
        {
            Stop(child);
        }
    }

    private static async Task<(string Output, string Error)> RunCommandAsync(string cli, string store, string[] args,
        CancellationToken ct)
    {
        using Process child = Process.Start(Info(cli, store, args)) ?? throw new InvalidOperationException("Could not start tooling.");
        Task<string> stdout = child.StandardOutput.ReadToEndAsync(ct);
        Task<string> stderr = child.StandardError.ReadToEndAsync(ct);
        try
        {
            await child.WaitForExitAsync(ct);
            string output = await stdout;
            string error = await stderr;
            return child.ExitCode == 0 ? (output, error) : throw new InvalidOperationException(output + error);
        }
        finally
        {
            Stop(child);
        }
    }

    private static ProcessStartInfo Info(string cli, string store, string[] args)
    {
        ProcessStartInfo info = new(cli) { RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment["CMAKEWORKFLOW_TOOL_STORE"] = store;
        foreach (string arg in args)
        {
            info.ArgumentList.Add(arg);
        }
        return info;
    }

    private static void Stop(Process child)
    {
        if (!child.HasExited)
        {
            child.Kill(entireProcessTree: true);
        }
    }

    private static void Check(bool condition, string description)
    {
        if (!condition)
        {
            throw new InvalidDataException(description);
        }
    }
}
