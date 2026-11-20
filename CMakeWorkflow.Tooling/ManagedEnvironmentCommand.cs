using System.Text;
using CMakeWorkflow.Tooling.Tools;

namespace CMakeWorkflow.Tooling;

internal static class ManagedEnvironmentCommand
{
    internal static async Task<int> LockAsync(string[] args, CancellationToken ct)
    {
        bool write = args is ["--write"];
        if (args.Length != 0 && !write)
        {
            throw new ArgumentException("Usage: tooling lock [--write]");
        }

        ManagedLinuxSdk.RequireHostAbi();
        byte[] bytes = ManagedEnvironmentLocks.Serialize();
        string digest = ManagedEnvironmentLocks.Hash(bytes);
        if (write)
        {
            await ManagedEnvironmentLocks.WriteAsync(bytes, digest, ct);
        }

        await Console.Out.WriteAsync(Encoding.UTF8.GetString(bytes));
        await Console.Error.WriteLineAsync("Environment lock SHA-256: " + digest);
        return 0;
    }

    internal static async Task<int> VcpkgAsync(string[] args, CancellationToken ct)
    {
        ManagedLinuxSdk.RequireHostAbi();
        if (args is ["plan", string project])
        {
            byte[] plan = VcpkgProjects.Serialize(await VcpkgProjects.PlanAsync(project, ct));
            await Console.Out.WriteAsync(Encoding.UTF8.GetString(plan));
            await Console.Error.WriteLineAsync("Vcpkg project plan SHA-256: " + ManagedEnvironmentLocks.Hash(plan));
            return 0;
        }

        if (args is ["install", string directory, "--yes", "--plan-sha", string approved])
        {
            VcpkgProjectPlan plan = await VcpkgProjects.PlanAsync(directory, ct);
            string digest = ManagedEnvironmentLocks.Hash(VcpkgProjects.Serialize(plan));
            if (!string.Equals(digest, approved, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("The vcpkg project plan differs from the approved SHA-256.");
            }

            await VcpkgProjects.InstallAsync(plan, digest, ct);
            await Console.Out.WriteLineAsync("Vcpkg project SHA-256: " + digest);
            return 0;
        }

        throw new ArgumentException("Usage: tooling vcpkg plan PROJECT | tooling vcpkg install PROJECT --yes --plan-sha SHA");
    }
}
