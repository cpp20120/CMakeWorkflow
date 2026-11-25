using System.Text;
using System.Text.Json;
using CMakeWorkflow.CMake.Build;
using CMakeWorkflow.Developer;

namespace CMakeWorkflow.Commands;

internal static class ProjectSdkResolution
{
    internal static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args is ["list", "--json"])
        {
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(DeveloperLifecycle.ListSdks(),
                ProjectSdkCatalogJson.Default.SdkCatalogDocument));
            return 0;
        }
        if (args is ["list"])
        {
            foreach (SdkDescriptor item in DeveloperLifecycle.ListSdks().Sdks.Where(item => item.Status != "abstract"))
            {
                await Console.Out.WriteLineAsync(item.Id + " " + item.Version + (item.Status == "available" ? "" : " " + item.Status));
            }
            return 0;
        }
        if (args is ["show", ..] or ["requirements", _, ..])
        {
            SdkDescriptor descriptor = args switch
            {
                ["show", string id] => DeveloperLifecycle.GetSdk(id, null),
                ["show", string id, "--json"] => DeveloperLifecycle.GetSdk(id, null),
                ["show", string id, "--version", string version] => DeveloperLifecycle.GetSdk(id, version),
                ["show", string id, "--version", string version, "--json"] => DeveloperLifecycle.GetSdk(id, version),
                ["requirements", string directory] => await DeveloperLifecycle.GetProjectSdkAsync(directory, ct),
                ["requirements", string directory, "--json"] => await DeveloperLifecycle.GetProjectSdkAsync(directory, ct),
                _ => throw new ArgumentException("Usage: project sdk show SDK [--version VERSION] [--json] | requirements DIRECTORY")
            };
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(descriptor, ProjectSdkCatalogJson.Default.SdkDescriptor));
            return 0;
        }

        if (args is not (["plan" or "verify" or "workflow", _] or ["lock", _, "--plan-sha", _]))
        {
            throw new ArgumentException("Usage: project sdk list | plan DIRECTORY | verify DIRECTORY | workflow DIRECTORY | lock DIRECTORY --plan-sha SHA256");
        }

        string root = Path.GetFullPath(args[1]);
        if (args[0] == "plan")
        {
            ProjectLockPlan plan = await DeveloperLifecycle.PlanSdkAsync(root, ct);
            await Console.Out.WriteLineAsync(Encoding.UTF8.GetString(plan.Json.Span));
            await Console.Error.WriteLineAsync("SDK plan SHA-256: " + plan.Sha256);
            return 0;
        }

        ProjectLockPlan current = args[0] == "lock"
            ? await DeveloperLifecycle.LockSdkAsync(root, args[3], ct)
            : await DeveloperLifecycle.VerifySdkAsync(root, ct);
        if (args[0] == "workflow")
        {
            await DeveloperLifecycle.WriteManagedSdkWorkflowAsync(root, ct);
        }
        else
        {
            await Console.Out.WriteLineAsync("SDK lock SHA-256: " + current.Sha256);
        }
        return 0;
    }

}
