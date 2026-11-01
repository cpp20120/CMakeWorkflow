$devcmd = 'C:\Program Files\Microsoft Visual Studio\18\Community\Common7\Tools\VsDevCmd.bat'

& $devcmd `
  '-no_logo' `
  '-arch=x64' `
  '-host_arch=x64' `
  '-winsdk=10.0.26100.0' `
  '-vcvars_ver=14.51.36231'

Write-Host "Exit code: $LASTEXITCODE"
