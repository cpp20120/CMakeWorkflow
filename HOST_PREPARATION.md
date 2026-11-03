# Host preparation

The `env doctor|plan|setup` pipeline uses existing Tooling profile discovery, package-manager installation, SHA-256
approval, revalidation and JSONL audit. Host settings are never modified by `plan`, `doctor` or a workflow run.

`env setup FILE --yes --plan-sha SHA --allow-system-changes` permits the explicitly listed settings, and does not
elevate privileges or silently run shell commands. Settings are nontransactional; a failure may leave partial changes,
recorded in the audit log.

Requirements (`schemaVersion: 1`, `toolProfiles: [...]`) may include:

* Linux: `maxPerfEventParanoid` and `cpuGovernor`. Governor changes target
  `/sys/devices/system/cpu/cpufreq/policy*/scaling_governor`.
* Windows: `windowsPowerScheme`, a canonical GUID of an **existing** power plan. For example,
  `381b4222-f694-41f0-9685-ff5bb260df2e` is the standard Balanced GUID, but availability of other schemes varies by
  machine. Windows uses `PowerGetActiveScheme`/`PowerSetActiveScheme` directly, not localized `powercfg` output. This
  setting is shared and may affect battery, thermals and other workloads. There is no automatic rollback.

Windows MSVC/SDK and WPR availability is handled by the existing Tooling profiles; this initial Windows host-settings
backend does not install Windows features or change ETW security policy. Linux-only and Windows-only settings cannot be
mixed. Tests use a fake backend and must also be run on a Windows CI runner; real power-plan mutation is deliberately
not part of automated testing.

## Windows development host checks

Set `requireWindowsMsvc`, `requireWindowsSdk`, and/or `requireWindowsWpr` to `true` in the environment requirements
JSON. These checks discover an installed MSVC x64 compiler using `vswhere` plus `cl.exe`, a Windows SDK with `rc.exe`/
`mt.exe`, and `wpr.exe`. They are observational; installing Visual Studio Build Tools or SDK components is not
automated.
`windows_etw_permissions` is deliberately `unverified`: the presence of WPR does **not** imply that a selected ETW
provider or kernel session can be traced. A WPR requirement therefore cannot be marked fully ready without a separate
authorized trace capability check.
