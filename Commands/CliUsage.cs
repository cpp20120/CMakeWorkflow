namespace CMakeWorkflow.Commands;

internal static class CliUsage
{
    internal static int Write()
    {
        Console.Error.WriteLine(
        "Usage: CMakeWorkflow remote <start|find|status|inspect|events|report|artifacts|download|cancel> --destination USER@HOST --endpoint REMOTE_SOCKET --workspace REMOTE_DIR ...\n" +
        "Usage: CMakeWorkflow host serve --endpoint PATH | host <doctor|fix> --endpoint PATH [--workspace DIR] [--profile NAME] [--json]; fix: [--yes|--dry-run] [--pm NAME] [--no-fallback]\n" +
        "Usage: CMakeWorkflow history <import|runs|show|metrics|query|compare|detect> ...\n" +
        "Usage: CMakeWorkflow env <doctor|plan|setup> REQUIREMENTS.json [--json] [--yes --plan-sha SHA256] [--allow-system-changes]\n" +
        "Usage: CMakeWorkflow tooling <list|doctor|plan|setup> [profile...] [--profile NAME] [--json] [--pm auto|none|apt|pacman|dnf|brew|winget] [--install-dir PATH] [--no-fallback] [--dry-run] [--yes]\n" +
        "Usage: CMakeWorkflow dagflow mutation run --suite NAME [--source-dir DIR]\n" +
        "       CMakeWorkflow dagflow fuzz seed --layer NAME [--source-dir DIR]\n" +
        "       CMakeWorkflow dagflow fuzz run --build-dir DIR --layer NAME [--duration SECONDS] [--corpus DIR] [--replay-only]\n" +
        "       CMakeWorkflow dagflow fuzz <triage|merge> --run-dir DIR [--coverage (merge only)]\n" +
        "       CMakeWorkflow dagflow weak-memory run --protocol NAME --relacy DIR\n" +
        "       CMakeWorkflow dagflow profile run --build-dir DIR --target NAME [--repeats N] [--backend auto|perf|etw|instruments] [--mode cpu|scheduler|counters|allocations]\n" +
        "       CMakeWorkflow dagflow profile run --variant NAME=DIR --variant NAME=DIR --target NAME [--repeats N]\n" +
        "       CMakeWorkflow dagflow profile analyze RUN_DIRECTORY\n" +
        "       CMakeWorkflow dagflow profile flamegraph PERF_DATA OUTPUT_JSON\n" +
        "       CMakeWorkflow dagflow profile render-historical DIRECTORY\n" +
        "       CMakeWorkflow dagflow profile run-historical --source-dir DIR --output DIR [--trials N] [--stat-trials N] [--jobs N] [--resume]\n" +
        "                  [--shuffle-algorithm ID] [--seeds 42,1337,2026] [--schedule-strategy ID] [--shuffle-scope block|global]\n" +
        "       CMakeWorkflow profile replay BASELINE_DIR CANDIDATE_DIR --node ID [--case NAME] [--harness INDEX] [--backend auto|perf|etw] [--results-dir DIR]\n" +
        "Usage: CMakeWorkflow context inspect BUILD_DIR [--json]\n" +
        "       CMakeWorkflow target <build|invoke|run|resolve> --build-dir DIR --target NAME [--config CONFIG] [--prepared] [--json for resolve] [-- executable-args...]\n" +
        "  build: request a known native executable/library target through CMake\n" +
        "  run:   build (or verify prepared target), then execute the binary\n" +
        "  resolve: only inspect the CMake File API codemodel; never build\n" +
        "  invoke: intentionally invoke any target, including utility/campaign targets\n" +
        "  --prepared: only for a CMake custom target with a DEPENDS edge\n" +
        "       CMakeWorkflow workflow sequence --build-dir DIR --first TARGET --second TARGET\n" +
        "         [--config CONFIG] [--first-arg ARG]... [--second-arg ARG]...\n" +
        "         [--controller-build-dir DIR] (only when invoked by a CMake build target)\n" +
        "  workflow sequence: build first, execute first, build second, execute second\n" +
        "       CMakeWorkflow workflow <plan|run> FILE [--only NODE]... [--jobs N] [--json] [--history-db PATH] [--no-history]\n" +
        "       CMakeWorkflow workflow generate benchmark --build-dir DIR --target TARGET --repeats N --timeout-seconds N --output FILE [--config CONFIG] [-- PROGRAM_ARG...]\n" +
        "       CMakeWorkflow workflow generate <profile|test-matrix|build-matrix|benchmark-matrix> [OPTIONS|--recipe FILE] --output FILE\n" +
        "       CMakeWorkflow workflow report RUN_DIRECTORY [--json]\n" +
        "       CMakeWorkflow workflow resume RUN_DIRECTORY\n" +
        "       CMakeWorkflow workflow reconcile RUN_DIRECTORY [--node ID] --decision succeeded|retry\n" +
        "         --reason TEXT --expected-checkpoint SHA256 --confirm-stopped [--json]\n" +
        "         [--results-dir DIR] [--controller-build-dir DIR]\n" +
        "  plan: validate and show the selected DAG without building or running\n" +
        "  run: execute dependencies, retain logs and write run.json");
        return 2;
    }
}
