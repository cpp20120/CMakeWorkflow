using System.Security.Cryptography;
using System.Text.Json;

namespace CMakeWorkflow.Tooling.Tools;

public static class ToolingPlanApproval
{
    public static string Compute(ToolingPlan plan) => Convert.ToHexStringLower(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(plan, ToolingJson.Default.ToolingPlan)));
}
