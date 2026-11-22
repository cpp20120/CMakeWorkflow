using System.Runtime.InteropServices;

namespace CMakeWorkflow.Tooling.Tools;

public static class ManagedLinuxSdk
{
    public const string TargetTriple = ManagedEnvironmentIdentity.TargetTriple;
    public const string Runtime = ManagedEnvironmentIdentity.Runtime;

    private const string MultiArch = "x86_64-linux-gnu";
    private static readonly Version MinimumGlibc = new(2, 31);

    [DllImport("libc", EntryPoint = "gnu_get_libc_version")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.SafeDirectories)]
    private static extern IntPtr GlibcVersion();

    public static void RequireHostAbi()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("Managed Clang and its SDK require Linux x86-64.");
        }

        string? actual;
        try
        {
            actual = Marshal.PtrToStringAnsi(GlibcVersion());
        }
        catch (Exception error) when (error is EntryPointNotFoundException or DllNotFoundException)
        {
            throw new PlatformNotSupportedException("Managed Clang and its SDK require a glibc host.", error);
        }

        if (!Version.TryParse(actual, out Version? version) || version < MinimumGlibc)
        {
            throw new PlatformNotSupportedException($"Managed Clang requires glibc 2.31 or later. Detected {actual ?? "unknown"}.");
        }
    }

    private static string GccRuntime(string sysroot) => Path.Combine(sysroot, "usr", "lib", "gcc", MultiArch, "10");

    internal static void CheckLayout(string sysroot)
    {
        foreach (string file in new[]
                 {
                     Path.Combine("usr", "include", "c++", "10", "iostream"),
                     Path.Combine("usr", "lib", MultiArch, "Scrt1.o"),
                     Path.Combine("usr", "lib", MultiArch, "crti.o"),
                     Path.Combine("lib", MultiArch, "libc.so.6"),
                     Path.Combine("lib64", "ld-linux-x86-64.so.2"),
                     Path.Combine(GccRuntime(sysroot), "crtbeginS.o"),
                     Path.Combine(GccRuntime(sysroot), "libstdc++.so")
                 })
        {
            if (!File.Exists(Path.Combine(sysroot, file)))
            {
                throw new InvalidDataException($"The pinned Linux SDK is missing {file}. Reapply the linux-clang tooling profile.");
            }
        }
    }

    public static string DriverFlags(string sysroot, string lld)
    {
        ArgumentNullException.ThrowIfNull(lld);
        return $"--no-default-config --gcc-install-dir={ShellQuote(GccRuntime(sysroot))} --ld-path={ShellQuote(lld)}";
    }

    private static string ShellQuote(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    internal static async Task CompileAndLinkAsync(string llvm, string sysroot, string directory, CancellationToken ct)
    {
        CheckLayout(sysroot);
        string source = Path.Combine(directory, "smoke.cpp");
        string program = Path.Combine(directory, "smoke");
        await File.WriteAllTextAsync(source,
            """
            #include <iostream>
            #include <string>
            int main() { const std::string message = "cmakeworkflow-sdk"; std::cout << message; }
            """, ct);

        SystemToolingRuntime runtime = new(directory);
        Dictionary<string, string> environment = new(StringComparer.Ordinal)
        {
            ["PATH"] = Path.Combine(llvm, "bin"),
            ["TMPDIR"] = directory,
            ["LC_ALL"] = "C"
        };
        ToolCommandResult linked = await runtime.RunAsync(Path.Combine(llvm, "bin", "clang++"),
            ["--no-default-config", "--target=" + TargetTriple, "--sysroot=" + sysroot,
                "--gcc-install-dir=" + GccRuntime(sysroot), "--ld-path=" + Path.Combine(llvm, "bin", "ld.lld"),
                "-Wl,-t", source, "-o", program], TimeSpan.FromSeconds(45), environment, ct);

        string[] inputs = linked.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (linked.ExitCode != 0 || !inputs.Any(p => p.EndsWith("/Scrt1.o", StringComparison.Ordinal)) ||
            !inputs.Any(p => p.EndsWith("/libc.so.6", StringComparison.Ordinal)) ||
            !inputs.Any(p => p.EndsWith("/libstdc++.so", StringComparison.Ordinal)) ||
            inputs.Any(p => !p.StartsWith(sysroot + "/", StringComparison.Ordinal) &&
                            !p.StartsWith(directory + "/", StringComparison.Ordinal) &&
                            !p.StartsWith(llvm + "/", StringComparison.Ordinal)))
        {
            throw new InvalidDataException("Clang/LLD failed the pinned SDK compile-and-link check: " + linked.Error.Trim());
        }

        ToolCommandResult executed = await runtime.RunAsync(program, [], TimeSpan.FromSeconds(15), environment, ct);
        if (executed.ExitCode != 0 || executed.Output != "cmakeworkflow-sdk")
        {
            throw new InvalidDataException($"The SDK-linked executable requires a compatible host runtime ({Runtime}): " +
                                           executed.Error.Trim());
        }
    }
}
