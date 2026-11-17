using CMakeWorkflow.CMake.Planning;
using CMakeWorkflow.Host.Profiling;
using CMakeWorkflow.Host.Protocol;
using CMakeWorkflow.Profiling.Historical;

namespace CMakeWorkflow.Host.Sessions;

public sealed partial class DoctorSession
{
    public Task<HistoricalProfileRenderResponse> HistoricalProfileRenderAsync(HistoricalProfileRenderRequest request,
        CancellationToken cancellationToken) =>
        ApplicationCallAsync(request, async (paths, ct) =>
        {
            string root = paths.Require(request.RunDirectory);
            void CheckPath(string path)
            {
                string resolved = paths.Require(path);
                string relative = Path.GetRelativePath(root, path);
                bool insideArchive = !Path.IsPathRooted(relative) && relative != ".."
                    && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
                if (insideArchive && !WorkflowPlanner.PathComparer.Equals(resolved, Path.GetFullPath(path)))
                {
                    throw new ApplicationRefusal("path_not_allowed", "Historical profile paths cannot be redirected.");
                }
            }

            int count = await DagFlowHistoricalReport.RenderReportAsync(root, CheckPath, paths.Read, ct);
            return new HistoricalProfileRenderResponse(1, root, Path.Combine(root, "index.html"),
                Path.Combine(root, "sampling-summary.json"), Path.Combine(root, "sampling-summary-fp.json"), count);
        }, cancellationToken);
}
