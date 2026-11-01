# Windows toolchain discovery — первый срез

В Host добавлены три typed RPC: `toolchains/discover`, `toolchains/resolve`, `toolchains/inspectEnvironment`. Все они доступны в `initialize.capabilities` и в .NET SDK через `client.Toolchains`.

- **Discover** (`{}`): кандидаты CMake/Ninja/MSVC/LLVM/MSBuild/Windows SDK из всех VS/Build Tools через `vswhere -products *`, а также из PATH (включая shims Scoop/winget) и известных standalone-путей. Возвращает источник и ID каждого кандидата.
- **Resolve** (`{ "selection": { "generator": "Ninja", "compilerCandidateId": "..." } }`): согласовывает CMake, build tool, compiler, SDK и `EnvironmentProfileId`. Приоритет PATH перед VS действует только при отсутствии явного выбора. ID перепроверяются сервером; случайные пути и смешивание разных VS instances запрещены.
- **InspectEnvironment** (`{ "environment": { "environmentProfileId": "vs:<id>", "hostArchitecture": "x64", "targetArchitecture": "x64" } }`): запускает штатный `VsDevCmd.bat` выбранной VS в отдельном дочернем `cmd.exe`, возвращает fingerprint и только разрешённые переменные окружения для диагностики. Окружение самого Host не меняется; случайные унаследованные переменные и секреты в ответ не включаются.

**Ограничение:** `resolved` означает, что найден структурно совместимый набор, но не подтверждает успешную компиляцию и ABI. `inspectEnvironment` остаётся read-only preview. Для исполнения добавлена **явная привязка** `WorkflowContext.toolchain`: Host заново разрешает кандидатов до dispatch, сохраняет только fingerprint в отчёте и передаёт параметры в дочерние процессы. Подробности: [execution binding](toolchain-execution.md). Старые Doctor/Fix и Environment Plan/Apply не меняют семантику.

Linux/macOS получают `unsupported` для Windows-specific discovery. Никакой установки SDK, записи в реестр, повышения прав или изменений глобального PATH нет.
