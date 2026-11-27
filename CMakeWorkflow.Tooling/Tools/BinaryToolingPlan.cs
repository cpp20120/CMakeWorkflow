using Capnp.Managed;
using CMakeWorkflow.Binary.Capnp;
using Wire = Capnp.Generated.ManagedToolchain;

namespace CMakeWorkflow.Tooling.Tools;

internal static class BinaryToolingPlan
{
    internal static byte[] Encode(ToolingPlan plan)
    {
        MessageBuilder message = new(ManagedToolchainWire.Limits);
        Wire.ToolingPlan.Builder root = Wire.ToolingPlan.InitRoot(message);
        root.SetFormatVersion(1);
        root.SetSchemaVersion((uint)plan.SchemaVersion);
        root.SetPlatform(plan.Platform.ToString());
        root.SetInstallDirectory(plan.InstallDirectory);
        root.SetPackageManager(plan.PackageManager.ToString());
        root.SetReady(plan.Ready);
        root.SetResolutionPolicy(plan.ResolutionPolicy?.ToString());
        Wire.ToolingPlan.ProfilesListBuilder profiles = root.InitProfiles(plan.Profiles.Length);
        for (int i = 0; i < profiles.Count; i++)
        {
            profiles.Set(i, plan.Profiles[i]);
        }

        Wire.ToolingPlan.ToolsListBuilder tools = root.InitTools(plan.Tools.Count);
        for (int i = 0; i < tools.Count; i++)
        {
            ToolStatus tool = plan.Tools[i];
            Wire.ToolStatus.Builder value = tools[i];
            value.SetId(tool.Id);
            value.SetDisplayName(tool.DisplayName);
            value.SetInstalled(tool.Installed);
            value.SetCommand(tool.Command);
            value.SetVersion(tool.Version);
            value.SetMinimumVersion(tool.MinimumVersion);
            value.SetDiagnostic(tool.Diagnostic);
            value.SetResolution(tool.Resolution);
            value.SetOrigin(tool.Origin);
            value.SetSha256(tool.Sha256);
            value.SetSystemResolution(tool.SystemResolution);
            value.SetSystemVersion(tool.SystemVersion);
        }

        Wire.ToolingPlan.StepsListBuilder steps = root.InitSteps(plan.Steps.Count);
        for (int i = 0; i < steps.Count; i++)
        {
            ToolingStep step = plan.Steps[i];
            Wire.ToolingStep.Builder value = steps[i];
            value.SetTool(step.Tool);
            value.SetMethod(step.Method);
            value.SetExecutable(step.Executable);
            value.SetSource(step.Source);
            value.SetDiagnostic(step.Diagnostic);
            Wire.ToolingStep.ArgumentsListBuilder arguments = value.InitArguments(step.Arguments.Length);
            for (int j = 0; j < arguments.Count; j++)
            {
                arguments.Set(j, step.Arguments[j]);
            }

            if (step.Release is { } release)
            {
                ManagedToolchainWire.WriteRelease(value.InitRelease(), release);
            }
        }

        return CapnpWire.Frame(CapnpWire.Canonicalize(message.CopySegment()));
    }

    internal static string Identity(ReadOnlySpan<byte> frame) =>
        ManagedToolchainWire.Identity("cmwf-tooling-provisioning-plan-v1\n"u8, frame);
}
