$devcmd = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat'

# 1. Базовая активация x64
& $devcmd -arch=x64 -host_arch=x64
"Base exit code: $LASTEXITCODE"

# 2. Активация с конкретными версиями SDK и MSVC
& $devcmd -arch=x64 -host_arch=x64 `
  -winsdk=10.0.26100.0 `
  -vcvars_ver=14.51.36231
"Pinned exit code: $LASTEXITCODE"