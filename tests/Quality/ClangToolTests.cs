using CMakeWorkflow.IntegrationTests.Support;
using static CMakeWorkflow.IntegrationTests.Support.TestHarness;

namespace CMakeWorkflow.IntegrationTests.Quality;

internal static class ClangToolTests
{
    internal static void Run()
    {
        using TempTree temp = new();
        string project = Path.Combine(temp.Path, "clang project ü");
        CopyFixture("clang-tools", project);
        string build = Path.Combine(project, "build");
        _ = Write(Path.Combine(build, ".cmake", "api", "v1", "query", "codemodel-v2"), "");
        _ = Command("cmake", ["-S", project, "-B", build, "-G", "Ninja", "-DCMAKE_CXX_COMPILER=clang++",
            "-DCMAKE_EXPORT_COMPILE_COMMANDS=ON"]);
        string[] target = ["--project", project, "--target", "runtime"];
        string header = Path.Combine(project, "runtime.hpp");
        string other = Path.Combine(project, "other.cpp");
        string headerBefore = File.ReadAllText(header);
        string otherBefore = File.ReadAllText(other);

        string check = Command(Cli, ["format", .. target, "--check", "--batch-size", "2"], expected: 1).Output;
        Check(check.Contains("4 files, 2 invocations", StringComparison.Ordinal) && File.ReadAllText(header) == headerBefore, check);
        _ = Command(Cli, ["format", .. target, "--apply", "--batch-size", "2"]);
        _ = Command(Cli, ["format", .. target, "--check"]);
        Check(File.ReadAllText(other) == otherBefore && File.ReadLines(header).Contains("int two();"), "Unexpected formatting scope");

        const string checks = "-*,modernize-use-nullptr";
        _ = Command(Cli, ["lint", .. target, "--checks", checks, "--jobs", "2"]);
        _ = Command(Cli, ["lint", .. target, "--tool", "clang-check", "--jobs", "2"]);
        _ = Command(Cli, ["analyze", .. target, "--preset", "clang-release", "--jobs", "2"]);
        Check(!Directory.Exists(Path.Combine(project, "preset-build")), "Analysis configured the ordinary preset build tree");

        string barrier = Directory.CreateDirectory(Path.Combine(temp.Path, "barrier")).FullName;
        _ = Command(Cli, ["lint", .. target, "--jobs", "2", "--executable", QualityToolProbe.AppHost], environment: new()
        {
            [QualityToolProbe.ModeVariable] = "tidy",
            [QualityToolProbe.MarkerVariable] = barrier
        });
        Check(Directory.EnumerateFiles(barrier).Count() == 3, "Expected one clang-tidy check per translation unit");
        _ = Command(Cli, ["lint", .. target, "--tool", "iwyu", "--jobs", "2", "--executable", QualityToolProbe.AppHost],
            environment: new() { [QualityToolProbe.ModeVariable] = "iwyu" });

        string source = Write(Path.Combine(project, "runtime three.cpp"), "int *pointer() { return 0; }\n");
        string fixes = Path.Combine(temp.Path, "fixes");
        string tidy = Command(Cli, ["lint", .. target, "--checks", checks, "--export-fixes", fixes], expected: 1).Output;
        Check(tidy.Contains("modernize-use-nullptr", StringComparison.Ordinal) &&
            File.ReadAllText(source).Contains("return 0;", StringComparison.Ordinal), tidy);
        _ = RunCli("lint", "--project", project, "--apply-replacements", fixes);
        Check(File.ReadAllText(source).Contains("return nullptr;", StringComparison.Ordinal), "Fixes were not applied");
        File.AppendAllText(source, "int bad() { int *p = nullptr; return *p; }\n");
        string analysis = Command(Cli, ["analyze", .. target, "--build-dir", build], expected: 1).Output;
        Check(analysis.Contains("clang-analyzer-core.NullDereference", StringComparison.Ordinal), analysis);
        Pass("Clang target selection, format batching, per-TU concurrency, static analysis and explicit fixes");
    }
}
