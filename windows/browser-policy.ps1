# Shared by install/uninstall. The ownership journal is saved before each mutation.
function Test-CareCarePolicyValue {
    param($Actual, $Expected, [string]$Kind)
    if ($Kind -eq 'DWord') { return [int64]$Actual -eq [int64]$Expected }
    return [string]::Equals([string]$Actual, [string]$Expected, [StringComparison]::Ordinal)
}

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
                $ownedKind = [string]$existing[0].OwnedKind
                if (!$present -or $kind -cne $ownedKind -or !(Test-CareCarePolicyValue $value $existing[0].OwnedValue $ownedKind)) {
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
        $ownedKind = [string]$entry.OwnedKind
        $currentValue = $key.GetValue($entry.Name)
        if ($key.GetValueKind($entry.Name).ToString() -cne $ownedKind -or
            !(Test-CareCarePolicyValue $currentValue $entry.OwnedValue $ownedKind)) { continue }
        if ($entry.Present) {
            New-ItemProperty $entry.Path -Name $entry.Name -Value $entry.Value -PropertyType $entry.Kind -Force | Out-Null
        } else { Remove-ItemProperty $entry.Path -Name $entry.Name }
    }
    Remove-Item $journalPath
}
