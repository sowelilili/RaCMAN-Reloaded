<#
.SYNOPSIS
    Allows RaCMAN Reloaded to receive the PS3's telemetry through Windows Firewall.

.DESCRIPTION
    qwark streams live state (readouts, toggle state, the pad for combos) to the PC over UDP.
    Windows Firewall blocks unsolicited inbound UDP for a freshly unzipped, unsigned app and
    often does so without showing the usual prompt, so the client silently falls back to slower
    TCP polling. This adds inbound allow rules (UDP and TCP) for RaCMAN.App.exe so the UDP path
    works.

    The script self-elevates: run it normally and it re-launches itself with an administrator
    prompt. It is safe to run again; it replaces its own rules rather than stacking them. It only
    touches rules named "RaCMAN Reloaded (...)", nothing else in the firewall.

    Remove the rules later with:  .\windows-firewall.ps1 -Remove
#>
[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'

$exe = Join-Path $PSScriptRoot 'RaCMAN.App.exe'
$rules = @(
    @{ Name = 'RaCMAN Reloaded (UDP-In)'; Protocol = 'UDP' },
    @{ Name = 'RaCMAN Reloaded (TCP-In)'; Protocol = 'TCP' }
)

# Re-launch elevated if we are not already administrator.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"")
    if ($Remove) { $args += '-Remove' }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $args
    }
    catch {
        Write-Warning 'Administrator approval was declined; no firewall change was made.'
    }
    return
}

foreach ($rule in $rules) {
    Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule -ErrorAction SilentlyContinue
}

if ($Remove) {
    Write-Host 'Removed the RaCMAN Reloaded firewall rules.'
    Start-Sleep -Seconds 2
    return
}

if (-not (Test-Path $exe)) {
    Write-Warning "RaCMAN.App.exe was not found next to this script ($exe)."
    Write-Warning 'Put this script in the same folder as the app and run it again.'
    Start-Sleep -Seconds 4
    return
}

foreach ($rule in $rules) {
    New-NetFirewallRule -DisplayName $rule.Name -Direction Inbound -Action Allow `
        -Program $exe -Protocol $rule.Protocol -Profile Any | Out-Null
    Write-Host "Allowed inbound $($rule.Protocol) for $exe"
}

Write-Host ''
Write-Host 'Done. RaCMAN Reloaded can now receive the console''s telemetry over UDP.'
Start-Sleep -Seconds 3
