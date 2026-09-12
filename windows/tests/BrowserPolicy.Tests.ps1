$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\..\browser-policy.ps1"

# Exercise the real registry provider in an isolated HKCU subtree; never touch
# browser policy keys or install filtering on the test runner.
$root = 'HKCU:\Software\CareCare.PolicyTests.' + [guid]::NewGuid().ToString('N')
$state = Join-Path ([IO.Path]::GetTempPath()) ('carecare-browser-tests-' + [guid]::NewGuid().ToString('N'))
$paths = @("$root\Chrome", "$root\Edge")
function Assert($condition, $message) { if (!$condition) { throw $message } }
try {
    New-Item -ItemType Directory $state | Out-Null
    New-Item $paths[0] -Force | Out-Null
    New-ItemProperty $paths[0] -Name ProxySettings -Value '{"ProxyMode":"system"}' -PropertyType String | Out-Null
    New-ItemProperty $paths[0] -Name QuicAllowed -Value 1 -PropertyType DWord | Out-Null
    New-ItemProperty $paths[0] -Name UnrelatedSetting -Value 'keep' -PropertyType String | Out-Null
    Set-CareCareBrowserPolicy -State $state -BrowserPaths $paths
    $journal = Get-Content "$state\browser-policy.json" -Raw
    Set-CareCareBrowserPolicy -State $state -BrowserPaths $paths
    Assert ((Get-Content "$state\browser-policy.json" -Raw) -eq $journal) 'Reinstall replaced the ownership backup.'
    $effective = (Get-ItemProperty $paths[0]).ProxySettings | ConvertFrom-Json
    Assert ($effective.ProxyMode -eq 'fixed_servers' -and $effective.ProxyServer -eq 'http://127.0.0.1:17843') 'Proxy policy was not applied.'
    Assert ($effective.ProxyBypassList -eq '<-loopback>') 'Implicit bypass was not disabled.'
    Assert ((Get-ItemProperty $paths[0]).QuicAllowed -eq 0) 'QUIC was not disabled.'
    Restore-CareCareBrowserPolicy -State $state
    Assert ((Get-ItemProperty $paths[0]).ProxySettings -eq '{"ProxyMode":"system"}') 'Original proxy policy was not restored.'
    Assert ((Get-ItemProperty $paths[0]).QuicAllowed -eq 1) 'Original QUIC policy was not restored.'
    Assert ((Get-ItemProperty $paths[0]).UnrelatedSetting -eq 'keep') 'Unrelated registry value was modified.'
    Assert ((Get-Item $paths[1]).GetValueNames() -notcontains 'ProxySettings') 'New owned value was not removed.'
    Restore-CareCareBrowserPolicy -State $state

    Set-CareCareBrowserPolicy -State $state -BrowserPaths $paths
    Set-ItemProperty $paths[1] -Name ProxySettings -Value 'external administrator edit'
    $rejected = $false
    try { Set-CareCareBrowserPolicy -State $state -BrowserPaths $paths }
    catch { $rejected = $true }
    Assert $rejected 'Reinstall overwrote an external policy change.'
    Restore-CareCareBrowserPolicy -State $state
    Assert ((Get-ItemProperty $paths[1]).ProxySettings -eq 'external administrator edit') 'Uninstall overwrote an external policy change.'
    Write-Host 'Browser policy apply, repeat install, restoration and external-edit checks passed.'
} finally {
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item $state -Recurse -Force -ErrorAction SilentlyContinue
}

foreach ($script in Get-ChildItem "$PSScriptRoot\.." -Filter '*.ps1' -Recurse) {
    $parseErrors = $null
    [System.Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$null, [ref]$parseErrors) | Out-Null
    if ($parseErrors.Count -gt 0) { throw ($parseErrors | Out-String) }
}
Write-Host 'PowerShell syntax checks passed.'
