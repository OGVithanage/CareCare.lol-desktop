#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$name = 'CareCareAllowlist'
$executable = "$env:ProgramFiles\CareCare\CareCare.Service.exe"
if (!(Test-Path $executable)) { throw 'Service executable is missing. Restore the published files before removing policy.' }
if (Get-Service $name -ErrorAction SilentlyContinue) { Stop-Service $name -Force }
& $executable --remove-policy
if ($LASTEXITCODE -ne 0) { throw 'Could not remove WFP rules. Service registration and files retained for recovery.' }
sc.exe delete $name
if ($LASTEXITCODE -ne 0) { throw 'Could not delete service registration' }
Write-Host 'CareCare filtering removed. Saved domains and binaries retained for reinstall or manual deletion.'
