using CMakeWorkflow.CMake.Build;

namespace CMakeWorkflow.CMake.Toolchains;

// vcpkg chainloads a verified toolchain that explicitly sets CMAKE_SYSTEM_NAME.
// The project's variant module needs to distinguish that native toolchain from
// actual cross-compilation, after CMake has identified the host and target.
internal static class NativeVcpkgProjectHook
{
    private const string FileName = ".cmwf-native-vcpkg-v1.cmake";
    private static ReadOnlySpan<byte> Content => """
        if(NOT CMAKE_HOST_SYSTEM_NAME STREQUAL "Linux"
           OR NOT CMAKE_HOST_SYSTEM_PROCESSOR STREQUAL "x86_64"
           OR NOT CMAKE_SYSTEM_NAME STREQUAL CMAKE_HOST_SYSTEM_NAME
           OR NOT CMAKE_SYSTEM_PROCESSOR STREQUAL CMAKE_HOST_SYSTEM_PROCESSOR
           OR NOT CMAKE_CXX_COMPILER_TARGET STREQUAL "x86_64-unknown-linux-gnu")
            message(FATAL_ERROR "The approved vcpkg Kit requires native Linux x86-64 GNU compilation")
        endif()
        set(CMAKE_CROSSCOMPILING FALSE)
        """u8;

    internal static async Task<string> PrepareAsync(string buildDirectory, CancellationToken ct)
    {
        string path = BuildVariantConfiguration.CheckedPath(buildDirectory, FileName);
        if (!File.Exists(path))
        {
            string staging = path + "." + Guid.NewGuid().ToString("N");
            try
            {
                await File.WriteAllBytesAsync(staging, Content.ToArray(), ct);
                ct.ThrowIfCancellationRequested();
                File.Move(staging, path);
            }
            finally
            {
                File.Delete(staging);
            }
        }
        return Verify(buildDirectory);
    }

    internal static string Verify(string buildDirectory)
    {
        string path = BuildVariantConfiguration.CheckedPath(buildDirectory, FileName);
        using FileStream stream = File.OpenRead(path);
        Span<byte> bytes = stackalloc byte[Content.Length];
        if (stream.Length != bytes.Length)
        {
            throw new InvalidDataException("The native vcpkg project hook differs from the approved implementation.");
        }
        stream.ReadExactly(bytes);
        return bytes.SequenceEqual(Content) ? path :
            throw new InvalidDataException("The native vcpkg project hook differs from the approved implementation.");
    }
}
