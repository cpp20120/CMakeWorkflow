namespace CMakeWorkflow;

/// <summary>
/// Minimal interleaved-build probe, NOT a general workflow scheduler.
/// A single external context owns both build operations and preserves their order.
/// </summary>
public static class TwoTargetWorkflow
{
    public static async Task<int> ExecuteAsync(string[] args, CancellationToken ct)
    {
        string? buildDir = null, config = null, first = null, second = null;
        string? controllerBuildDir = null;
        var firstArgs = new List<string>();
        var secondArgs = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--build-dir" when i + 1 < args.Length: buildDir = args[++i]; break;
                case "--controller-build-dir" when i + 1 < args.Length: controllerBuildDir = args[++i]; break;
                case "--config" when i + 1 < args.Length: config = args[++i]; break;
                case "--first" when i + 1 < args.Length: first = args[++i]; break;
                case "--second" when i + 1 < args.Length: second = args[++i]; break;
                case "--first-arg" when i + 1 < args.Length: firstArgs.Add(args[++i]); break;
                case "--second-arg" when i + 1 < args.Length: secondArgs.Add(args[++i]); break;
                default: throw new ArgumentException($"Invalid workflow sequence option: {args[i]}");
            }
        }
        if (string.IsNullOrWhiteSpace(buildDir) || string.IsNullOrWhiteSpace(first) ||
            string.IsNullOrWhiteSpace(second))
            throw new ArgumentException("workflow sequence requires --build-dir, --first and --second.");

        // Canonicalize each component before comparing directories: Path.GetFullPath
        // only removes lexical ".." and does not resolve symlinks or junctions.
        var workerDir = CanonicalExistingDirectory(buildDir);
        if (controllerBuildDir is not null)
        {
            var controllerDir = CanonicalExistingDirectory(controllerBuildDir);
            if (string.Equals(workerDir, controllerDir,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "Unsafe nested build: the CMake controller and worker build directories are identical. " +
                    "Use a different configured worker build tree or launch the CLI outside Ninja.");
        }

        BuildTreeOwnership.RejectController(workerDir, controllerBuildDir);
        using var ownership = await BuildTreeOwnership.AcquireAsync([workerDir], ct);
        var builder = new ExternalCMakeBuildContext();
        var firstTarget = CMakeConfiguration.Resolve(new CMakeTargetRef(workerDir, first, config));
        var secondTarget = CMakeConfiguration.Resolve(new CMakeTargetRef(workerDir, second, config));

        Console.Error.WriteLine($"[CMakeWorkflow] sequence 1/4: build {firstTarget.Target}");
        var firstArtifact = await builder.EnsureBuiltAsync(firstTarget, ct);
        Console.Error.WriteLine($"[CMakeWorkflow] sequence 2/4: run {firstArtifact.ExecutablePath}");
        await RunArtifactAsync(firstArtifact, firstArgs, ct);

        // Crucially, do NOT pre-build the second target. Its compilation happens
        // AFTER the first executable finishes, even when both use one build tree.
        Console.Error.WriteLine($"[CMakeWorkflow] sequence 3/4: build {secondTarget.Target}");
        var secondArtifact = await builder.EnsureBuiltAsync(secondTarget, ct);
        Console.Error.WriteLine($"[CMakeWorkflow] sequence 4/4: run {secondArtifact.ExecutablePath}");
        await RunArtifactAsync(secondArtifact, secondArgs, ct);
        Console.Error.WriteLine("[CMakeWorkflow] sequence completed");
        return 0;
    }

    // Resolve the *stored* link target component-by-component. ResolveLinkTarget().FullName
    // may normalize ".." before following an intermediate symlink, which changes the
    // physical result (e.g. alias -> to-child/.. where to-child is itself a link).
    // Only existing directories are accepted; unknown paths must fail closed.
    internal static string CanonicalExistingDirectory(string path, bool allowMissing = false)
    {
        if (!Path.IsPathFullyQualified(path))
        {
            if (Path.IsPathRooted(path))
                throw new ArgumentException($"Build directory must be fully qualified: {path}");
            path = Path.Combine(Environment.CurrentDirectory, path);
        }

        var root = Path.GetPathRoot(path)
            ?? throw new ArgumentException($"Build directory has no root: {path}");
        var current = root;
        var pending = new LinkedList<string>(SplitComponents(path));
        var links = 0;

        while (pending.First is not null)
        {
            var part = pending.First.Value;
            pending.RemoveFirst();
            if (part == ".") continue;
            if (part == "..")
            {
                current = Directory.GetParent(current)?.FullName ?? current;
                continue;
            }

            var next = Path.Combine(current, part);
            // LinkTarget gives the stored link text (including any ".." segments).
            // Do not use ResolveLinkTarget(...).FullName here: it may have already
            // lexically collapsed those segments.
            var linkTarget = new DirectoryInfo(next).LinkTarget;
            if (linkTarget is not null)
            {
                if (++links > 64)
                    throw new IOException($"Too many directory links while resolving '{path}'.");
                if (Path.IsPathRooted(linkTarget) && !Path.IsPathFullyQualified(linkTarget))
                    throw new IOException($"Ambiguous rooted link target: {linkTarget}");
                if (Path.IsPathFullyQualified(linkTarget))
                    current = Path.GetPathRoot(linkTarget)!;
                // For a relative link target, 'current' is already its physical
                // parent. For an absolute one, start at the target's own root.
                var components = SplitComponents(linkTarget);
                for (var i = components.Length - 1; i >= 0; i--)
                    pending.AddFirst(components[i]);
                continue;
            }

            if (!Directory.Exists(next) && !allowMissing)
                throw new DirectoryNotFoundException($"Build directory does not exist: {next}");
            current = next;
        }

        return Path.TrimEndingDirectorySeparator(current);
    }

    private static string[] SplitComponents(string path)
    {
        var root = Path.GetPathRoot(path);
        return path[(root?.Length ?? 0)..].Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task RunArtifactAsync(BuiltArtifact artifact,
        IReadOnlyList<string> args, CancellationToken ct)
    {
        if (!File.Exists(artifact.ExecutablePath))
            throw new FileNotFoundException("Built executable is missing.", artifact.ExecutablePath);
        await ProcessRunner.RunAsync(artifact.ExecutablePath, args,
            artifact.TargetRef.BuildDirectory, ct);
    }
}
