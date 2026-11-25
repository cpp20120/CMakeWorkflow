# CMakeWorkflow

CMakeWorkflow is a cross-platform workflow orchestration engine for native development.

Built on .NET 10, it coordinates CMake builds, tests, benchmarks, profiling, remote execution, and external CI workflows through a unified execution model.

It provides explicit DAG planning, durable operation tracking, crash recovery, provenance-aware artifacts, and performance history. Execution is available through a CLI, a persistent Host, and a typed .NET Client SDK.

CMake and Ninja manage native builds. CMakeWorkflow coordinates the operations around them.

## Documentation

- [Getting started](docs/en/getting-started.md) and [CLI reference](docs/en/cli.md)
- [Compare two C++ builds in History](docs/en/history-compare-example.md) · [Пример сравнения сборок](docs/ru/history-compare-example.md)
- [Ready-to-run JSON workflows](examples/README.md)
- [Author workflows in C#](docs/en/scripting.md) · [русская версия](docs/ru/scripting.md)
- [Developer Lifecycle API and CLI](docs/en/developer-lifecycle.md) · [русская версия](docs/ru/developer-lifecycle.md)
- [Engineering Kits](docs/en/engineering-kits.md) · [русская версия](docs/ru/engineering-kits.md)
- [Project SDK and managed toolchain quickstart](docs/en/project-sdk.md)
- [Native Runtime, Interop, Qt and Graphics SDKs](docs/en/sdk-ecosystem.md)
- [Compiler cache profiles and build statistics](docs/en/compiler-cache.md)
- [Artifact storage, action reuse and History summaries](docs/en/artifact-cas.md)
- [Host RPC and .NET workflow API](docs/en/workflow-authoring-api.md), [operation and History read API](docs/en/host-read-api.md)
- [Run a pinned Git experiment over SSH with vcpkg dependencies](docs/en/experiments.md#run-an-experiment-with-pinned-vcpkg-dependencies)
- [Use the .NET SDK from a separate project](docs/en/sdk-quickstart.md)
- [VS Code integration](docs/en/integrations.md#vs-code-extension)
- [Документация на русском](docs/ru/README.md)
