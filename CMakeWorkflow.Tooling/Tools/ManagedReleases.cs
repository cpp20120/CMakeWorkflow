#if EXPERIMENT_SDK
namespace CMakeWorkflow.Client.Experiments;
#else
namespace CMakeWorkflow.Tooling.Tools;
#endif

internal static class ManagedReleases
{
    internal const string Platform = "linux-x64";
    internal static ManagedToolRelease CMakeRelease { get; } = new("cmake", "4.1.2", Platform,
        "https://github.com/Kitware/CMake/releases/download/v4.1.2/cmake-4.1.2-linux-x86_64.tar.gz",
        "773cc679c3a7395413bd096523f8e5d6c39f8718af4e12eb4e4195f72f35e4ab",
        "e4cfa37816fc2e84fc9f6534beeb72149966085c84240d13c03ae67b438f13e1",
        "cmake-4.1.2-linux-x86_64");

    internal static ManagedToolRelease NinjaRelease { get; } = new("ninja", "1.13.1", Platform,
        "https://github.com/ninja-build/ninja/releases/download/v1.13.1/ninja-linux.zip",
        "0830252db77884957a1a4b87b05a1e2d9b5f658b8367f82999a941884cbe0238",
        "5c8e393cce8ef69a21ae3889db04b419abf52801151d78ffd39df9a05dc454b2",
        null);

    // Chromium 138.0.7204.49 compiler package and Chromium's Bullseye amd64 link sysroot.
    internal static ManagedToolRelease LlvmRelease { get; } = new("llvm", "21.0.0", Platform,
        "https://commondatastorage.googleapis.com/chromium-browser-clang/Linux_x64/clang-llvmorg-21-init-11777-gfd3fecfc-1.tar.xz",
        "5f69279b3697166facfc354634157e0a8a32fa6e36864200ad8a8f85add3f3f6",
        "15b40a305d21a2b70a825be3fca704b57ba75583de0c821efc1de5e0618ad4e7", "");

    internal static ManagedToolRelease SysrootRelease { get; } = new("sysroot", "debian-bullseye-glibc-2.31",
        Platform,
        "https://commondatastorage.googleapis.com/chrome-linux-sysroot/f851add76bdae472fb6c94b4f920bc4a132d85ac607951391297f636af7e934e",
        "f851add76bdae472fb6c94b4f920bc4a132d85ac607951391297f636af7e934e",
        "a4278a1c39844aa493707d21c8a87167ba606277b39cab706b425e47433fe36a", "");

    internal const string VcpkgCommit = "84bab45d415d22042bd0b9081aea57f362da3f35";

    internal static ManagedToolRelease VcpkgRegistry { get; } = new("vcpkg-registry", VcpkgCommit, Platform,
        "https://codeload.github.com/microsoft/vcpkg/tar.gz/" + VcpkgCommit,
        "e8731530753279ac15e760c010b1724439afdf057c4fb6cb2ffc04f7680a4953",
        "174a71d0508accfc0670ae8cf9fb34c1bd4a383be8d9bce5f763d2a422008b74", "vcpkg-" + VcpkgCommit);

    internal static ManagedToolRelease VcpkgRelease { get; } = new("vcpkg", "2025-12-05", Platform,
        "https://github.com/microsoft/vcpkg-tool/releases/download/2025-12-05/vcpkg-glibc",
        "54098727c0bcc338308dd42c7a94427816f9127e8393864684b7d9cdebbfe351",
        "65cf780c53fd2d9f19a691b40afe7f776d0d4c088fbbe91a84a62d37374cd331", null);
}
