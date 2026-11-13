# CMakeWorkflow

CMakeWorkflow is a cross-platform workflow orchestration engine for native development.

Built on .NET 10, it coordinates CMake builds, tests, benchmarks, profiling, remote execution, and external CI workflows through a unified execution model.

It provides explicit DAG planning, durable operation tracking, crash recovery, provenance-aware artifacts, and performance history. Execution is available through a CLI, a persistent Host, and a typed .NET Client SDK.

CMake and Ninja manage native builds. CMakeWorkflow coordinates the operations around them.

## Documentation

- [Getting started](docs/en/getting-started.md) and [CLI reference](docs/en/cli.md)
- [Host RPC and .NET workflow API](docs/en/workflow-authoring-api.md), [operation and History read API](docs/en/host-read-api.md)
- [VS Code integration](docs/en/integrations.md#vs-code-extension)
- [Документация на русском](docs/ru/README.md)
