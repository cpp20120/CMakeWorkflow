# Toolchain management

[Русский](../ru/toolchain-management.md)

Run tooling commands on the execution host. A controller can author a Project
SDK policy for another machine, but the workflow host resolves and verifies
the selected environment before it configures or uses a build tree.

```sh
cmakeworkflow tooling catalog
cmakeworkflow tooling installed
cmakeworkflow tooling resolve --profile linux-clang --version ninja=1.13.1
cmakeworkflow tooling plan --profile linux-clang --tool-policy ManagedOnly
cmakeworkflow tooling setup --profile linux-clang --tool-policy ManagedOnly --yes --plan-sha PLAN_SHA256
cmakeworkflow tooling lock --profile linux-clang --approve-sha ENVIRONMENT_SHA256
cmakeworkflow tooling verify --environment-lock ENVIRONMENT_SHA256

cmakeworkflow project new library Example ./Example --environment-lock ENVIRONMENT_SHA256
cmakeworkflow project sdk workflow ./Example > ./Example/workflow.pinned.json
cmakeworkflow workflow run ./Example/workflow.pinned.json
```

Use the digest from `plan` to approve provisioning, and the
`environmentSha256` from `resolve` to publish an environment lock. Repeat
exact `--version COMPONENT=VERSION` pins during review, setup and lock
publication. There is no automatic installation during resolve, verify,
or workflow admission. A missing managed component requires provisioning.
A corrupt installation is refused. Repair and removal are separate work.

The built-in catalog describes available releases, including installation
format, paths relative to the payload, companion components and runtime
requirements. The resolver chooses the profile's default release or an
exact version and checks host and target compatibility. Tooling applies
approved download plans using the existing per-artifact leases and
verified side-by-side store. Host admission materializes paths from its own
verified store and retains the existing machine-specific build and recovery
fingerprint. The portable lock contains component identities, with no
installation paths or host environment values.

| Provider / execution host | Discovery and selection | Provisioning | Workflow binding |
| --- | --- | --- | --- |
| Managed Linux x64, glibc ≥ 2.31 | Built-in CMake/CTest/CPack, Ninja, LLVM, Debian sysroot, vcpkg and registry | SHA-256-approved managed releases | Native GNU x64 Clang, Ninja or Ninja Multi-Config, locks v1/v2 |
| Linux x64 system tools | `resolve --profile cmake-ninja --tool-policy AllowSystem` inventories CMake, CTest and Ninja with their origin | Existing tooling only | Existing explicit Linux profiles. No portable lock for system tools |
| Windows x64 installed MSVC and Windows SDK | `tooling installed` uses existing Windows candidate discovery | Existing Visual Studio provisioning flow | Existing Windows x64 Ninja profiles. No v2 lock |
| macOS | Catalog and standalone Project SDK | Existing platform tooling | Ambient workflows. No managed v2 execution provider |

The catalog can represent distinct hosts and targets. The managed resolver
currently admits native Linux x64 only. Cross-compilation, managed GCC,
Apple SDK installation and Conan are future providers. Registry and package
receipts remain separate from compiler environments. vcpkg experiments keep
their original v1 identity and separate dependency approval. The command
`tooling lock --write` still publishes the original v1 bytes and digest.
Existing workflow and RPC schemas are unchanged.

In a Project SDK project, `settings.toolchain` can be declared in a shared
policy file or overridden as a unit in the project manifest. It records
`profile`, `policy`, `generator`, `target` and `environmentLockSha256`.
The SDK evaluates and locks these settings independently of tooling.
`project sdk workflow` verifies the SDK lock before emitting a workflow.
An ordinary CMake preset continues to build with the tools selected by CMake.
Configuration composition across compiler, architecture, build type,
sanitizers, linkage and optimization belongs to P3.
