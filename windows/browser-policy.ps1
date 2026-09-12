# Shared by install/uninstall. The ownership journal is saved before each mutation.
function Set-CareCareBrowserPolicy {
    param(
        [string]$State,
        [string[]]$BrowserPaths = @('HKLM:\SOFTWARE\Policies\Google\Chrome', 'HKLM:\SOFTWARE\Policies\Microsoft\Edge')
    )
    $journalPath = Join-Path $State 'browser-policy.json'
    $entries = @()
    if (Test-Path $journalPath) { $entries = @(Get-Content $journalPath -Raw | ConvertFrom-Json) }
    $proxy = '{"ProxyMode":"fixed_servers","ProxyServer":"http://127.0.0.1:17843","ProxyBypassList":"<-loopback>"}'
    foreach ($path in $BrowserPaths) {
        foreach ($setting in @(@{ Name = 'ProxySettings'; Value = $proxy; Kind = 'String' }, @{ Name = 'QuicAllowed'; Value = 0; Kind = 'DWord' })) {
            $existing = @($entries | Where-Object { $_.Path -eq $path -and $_.Name -eq $setting.Name })
            $key = Get-Item $path -ErrorAction SilentlyContinue
            $present = $null -ne $key -and $key.GetValueNames() -contains $setting.Name
            $value = if ($present) { $key.GetValue($setting.Name) } else { $null }
            $kind = if ($present) { $key.GetValueKind($setting.Name).ToString() } else { $null }
            if ($existing.Count -gt 0) {
                if (!$present -or $value -ne $existing[0].OwnedValue -or $kind -ne $existing[0].OwnedKind) {
                    throw "Browser policy changed outside CareCare: $path\$($setting.Name). Resolve this conflict before reinstalling."
                }
            } else {
                $entries += [pscustomobject]@{ Path = $path; Name = $setting.Name; Present = $present; Value = $value; Kind = $kind; OwnedValue = $setting.Value; OwnedKind = $setting.Kind }
                ConvertTo-Json -InputObject @($entries) -Depth 5 | Set-Content "$journalPath.tmp" -Encoding UTF8
                Move-Item "$journalPath.tmp" $journalPath -Force
            }
            New-Item $path -Force | Out-Null
            New-ItemProperty $path -Name $setting.Name -Value $setting.Value -PropertyType $setting.Kind -Force | Out-Null
        }
    }
}

function Restore-CareCareBrowserPolicy {
    param([string]$State)
    $journalPath = Join-Path $State 'browser-policy.json'
    if (!(Test-Path $journalPath)) { return }
    $entries = @(Get-Content $journalPath -Raw | ConvertFrom-Json)
    foreach ($entry in $entries) {
        $key = Get-Item $entry.Path -ErrorAction SilentlyContinue
        if ($null -eq $key -or $key.GetValueNames() -notcontains $entry.Name) { continue }
        # Preserve subsequent edits made by administrators or other management software.
        if ($key.GetValue($entry.Name) -ne $entry.OwnedValue -or $key.GetValueKind($entry.Name).ToString() -ne $entry.OwnedKind) { continue }
        if ($entry.Present) {
            New-ItemProperty $entry.Path -Name $entry.Name -Value $entry.Value -PropertyType $entry.Kind -Force | Out-Null
        } else { Remove-ItemProperty $entry.Path -Name $entry.Name }
    }
    Remove-Item $journalPath
}
