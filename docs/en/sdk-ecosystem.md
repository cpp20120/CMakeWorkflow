# Native Project SDK ecosystem

[Русский](../ru/sdk-ecosystem.md)

```sh
cmakeworkflow project sdk list --json
cmakeworkflow project sdk show Native.Runtime --version 1.0.0 --json
cmakeworkflow project new runtime DemoRuntime ./DemoRuntime --sdk-version 1.0.0 --variants
cmakeworkflow project new interop DemoAbi ./DemoAbi --variants
cmakeworkflow project new qt DemoQt ./DemoQt --variants
cmakeworkflow project new graphics DemoGraphics ./DemoGraphics --variants
cmakeworkflow project sdk requirements ./DemoGraphics
```

The built-in catalog holds exact SDK references, single inheritance, project
shape, default policy, execution modes, capabilities, dependency requirements,
vendored module and scaffold inventories, and recipe availability. A version
omission selects the highest built-in version. Project manifests always record
the chosen exact version. The CLI and workflow admission use the same catalog.
The Host checks the SDK reference, descriptor digest and shipped module hashes
in the configuration evidence. Toolchain and variant constraints still apply.

Legacy Library and Application 1.1.0 keep their original vendored modules,
SDK lock shape, and P3 variant identity. The new SDKs use
`cmake/cmwf-sdk/ecosystem-v1`, with one common CMake policy and native target
module and separate Runtime, Interop, Qt and Graphics components. They reuse
the unchanged P3 variant extension. A scaffold is built in staging and
published as one directory. Projects are upgraded only by explicit editing
and approval of their SDK inputs.

| SDK | Configurations and execution | External requirements and packaging |
| --- | --- | --- |
| Runtime 1.0.0 | Native ambient and managed Linux x64 Clang. Static/shared, CTest, benchmarks and existing profiler recipes. GCC on Linux can use the P3 sanitizer profile. | Optional CMake Threads, enabled by default. Install/export and TGZ for uninstrumented variants. |
| Interop 1.0.0 | Native C ABI, C/C++ tests and static/shared variants. Installed .NET 10 consumer against a shared library, enabled explicitly. | The library owns handle allocation and release. Install/export and TGZ. SDK instrumentation is disabled for the ABI example. |
| Qt 1.0.0 | Ambient Ninja. Widgets app, real offscreen event loop and Qt Test. Debug/Release variants. | Qt 6.3+ Core, Widgets, Test, moc/uic/rcc and offscreen plugin. No managed dependency installation or deployment package. |
| Graphics 1.0.0 | Ambient Ninja. Vulkan loader linkage, variant-local SPIR-V, CTest and optional Vulkan instance test. | Vulkan headers/loader and glslc. Optional software or hardware ICD. No deployment package. |
| Embedded 1.0.0 | Unsupported provider boundary. | Future cross compiler, target triple/ABI, sysroot, linker script, startup and image format, plus separately admitted emulator and hardware transport. |

SDK planning and requirements inspection only read project and embedded files.
CMake discovers Qt and Vulkan during configure. Set `Qt6_DIR`,
`CMAKE_PREFIX_PATH` or `VULKAN_SDK` for standalone CMake as appropriate,
and `CMWF_GLSLC` for an explicit shader compiler. Qt and Graphics use the
ambient environment. Their configure logs record host-local dependency
fingerprints, and reconfiguring the same tree with changed discovery evidence
fails. This evidence covers the declared discovery inputs and does not
constitute a portable dependency lock. The SDK, P2 environment and P3
configuration locks keep separate identities. Existing vcpkg experiments
continue to use their own explicit dependency approval and receipt.

The acceptance workflow installs domain-test dependencies explicitly on
Ubuntu 24.04 and logs the installed package versions with `dpkg-query`.
The Graphics job reports the exact `libvulkan-dev`, glslc and Mesa versions
and uses Mesa's software ICD. Local Linux verification used Qt 6.12.0,
Vulkan 1.4.363 and glslc with Shaderc 2026.4.
The product performs no equivalent installation during new, plan, or configure.

## Build a matrix

The Runtime scaffold supplies a Debug/Release static/shared matrix and an
`asan` profile. Use the ordinary P3 workflow commands.

```sh
cd DemoRuntime
cmakeworkflow project config plan . --matrix
cmakeworkflow project config lock . --matrix --plan-sha APPROVED_SHA256
cmakeworkflow project config workflow . --matrix > variants.json
cmakeworkflow workflow run variants.json --jobs 2 --results-dir out/results
```

The generated workflow has explicit configure, build and CTest branches.
Qt uses CMake's MOC/UIC/RCC integration. Graphics uses a standard CMake OUTPUT,
DEPENDS and shader include depfile. No SDK introduces its own executor.
Existing benchmark and profiler commands work on the Runtime benchmark
target. The generated Runtime README has commands for two measured runs,
History comparison and profiling. For an Interop shared library and its
C# consumer, run the generated README example with an installed .NET 10 SDK.

P5 can compose the catalog and existing commands into a unified developer
interface. Embedded execution and dependency provisioning for Qt and Vulkan
require separate provider work.
