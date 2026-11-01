# Привязка Windows toolchain к исполнению workflow

В `contexts.<имя>.toolchain` можно явно указать candidate IDs из
`toolchains/discover`. В `target/prepare` поле называется `toolchain`, а в
`profile/prepare` у каждого варианта — `variants[].toolchain`.

**Это opt-in.** Старые workflow без привязки не меняют поведение.

Host до авторизации внешнего dispatch заново проверяет установленный VS/Build Tools,
CMake, Ninja, компилятор и SDK, получает environment из штатного `VsDevCmd`
и связывает его с одним физическим build tree. `configure` использует выбранный
CMake, Ninja и C++ компилятор; `build/run/invoke/ctest/profile` получают те же
переменные через `ProcessStartInfo.Environment`, а не глобальный PATH Host.

Существующий CMakeCache проверяется на совпадение generator/compiler/Ninja:
автоматического переключения toolchain для готовой build tree нет.
В `run.json` записываются только toolchain identity и fingerprint; сырые
переменные окружения не сохраняются. Перед resume fingerprint сравнивается с
checkpoint; несовпадение запрещает дальнейшее исполнение.

**Ограничения:** пока Windows, Ninja/Ninja Multi-Config, host x64 -> target x64.
Visual Studio generator и кросс-компиляция потребуют отдельного механизма.
Привязка вместе с `configurePreset` пока запрещена: preset может выбрать другой
компилятор. В текущем срезе фиксируется C++ compiler, а C toolchain и ABI
окончательно проверяются CMake. Это не устанавливает SDK и не подменяет
`tooling/doctor`, `tooling/planFix` или `environment/apply`.
