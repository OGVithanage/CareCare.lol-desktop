#Requires -RunAsAdministrator
param(
    [Parameter(Mandatory = $true)][string]$ControllerAccount,
    [string]$Source = $(if (Test-Path "$PSScriptRoot\CareCare.Service.exe") { $PSScriptRoot } else { "$PSScriptRoot\publish" })
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\browser-policy.ps1"
$name = 'CareCareAllowlist'
$destination = "$env:ProgramFiles\CareCare"
$state = "$env:ProgramData\CareCare"
$sid = ([System.Security.Principal.NTAccount]::new($ControllerAccount)).Translate([System.Security.Principal.SecurityIdentifier]).Value
if (!(Test-Path "$Source\CareCare.Service.exe") -or !(Test-Path "$Source\CareCareWfp.dll")) { throw 'Run build.ps1 first.' }
$manifest = Get-Content "$Source\service-manifest.json" -Raw | ConvertFrom-Json
if ($manifest.protocolVersion -ne 2 -or $manifest.proxyPort -ne 17843) { throw 'Build a compatible version 2 service before installing.' }
New-Item -ItemType Directory -Force $destination, $state | Out-Null
# Only SYSTEM and administrators may change service binaries, configuration, or saved policy.
foreach ($directory in @($destination, $state)) {
    $acl = [System.Security.AccessControl.DirectorySecurity]::new()
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($principal in @('S-1-5-18', 'S-1-5-32-544')) {
        $rule = [System.Security.AccessControl.FileSystemAccessRule]::new(
            [System.Security.Principal.SecurityIdentifier]::new($principal), 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow')
        $acl.AddAccessRule($rule)
    }
    Set-Acl -Path $directory -AclObject $acl
}
$existing = Get-Service -Name $name -ErrorAction SilentlyContinue
if ($existing) { Stop-Service $name -Force }
Copy-Item "$Source\*" $destination -Recurse -Force
Copy-Item "$PSScriptRoot\browser-policy.ps1" $destination -Force
@{ controllerSid = $sid; protocolVersion = 2 } | ConvertTo-Json | Set-Content "$state\service.json" -Encoding Ascii
if (!$existing) {
    New-Service -Name $name -BinaryPathName ('"' + $destination + '\CareCare.Service.exe"') -DisplayName 'CareCare Website Allowlist' -StartupType Automatic -DependsOn BFE | Out-Null
} else { Set-Service $name -StartupType Automatic }
sc.exe failure $name reset= 86400 actions= restart/5000/restart/10000/restart/30000
if ($LASTEXITCODE -ne 0) { throw 'Could not set service recovery actions' }
sc.exe failureflag $name 1
if ($LASTEXITCODE -ne 0) { throw 'Could not enable service recovery' }
Set-CareCareBrowserPolicy -State $state
Start-Service $name
Write-Host "Installed. Only $ControllerAccount, administrators, and SYSTEM can update policy. Restart Chrome/Edge to load the managed proxy policy. Open Electron to synchronize the list."
