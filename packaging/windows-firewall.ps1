<#
.SYNOPSIS
    Allows RaCMAN Reloaded to receive the PS3's telemetry through Windows Firewall.

.DESCRIPTION
    qwark streams live state (readouts, toggle state, the pad for combos) to the PC over UDP on a
    port the client picks per connection. Windows Firewall blocks unsolicited inbound UDP for a
    freshly unzipped, unsigned app and often does so without showing the usual prompt, so the client
    silently falls back to slower TCP polling. This adds inbound allow rules (UDP and TCP) for
    RaCMAN.App.exe so the UDP path works.

    A firewall rule names one executable by its full path. The client passes the path of the process
    that actually owns the socket with -Program; run by hand, the script uses RaCMAN.App.exe beside
    itself. Under the installer that is ...\current\RaCMAN.App.exe, which keeps its name across
    updates, and not the stub launcher in the folder above it.

    The script self-elevates: run it normally and it re-launches itself with an administrator
    prompt. It is safe to run again; it replaces its own rules rather than stacking them, and it
    leaves alone a rule of the same name that belongs to another copy of RaCMAN that still exists,
    so allowing one copy no longer un-allows another. Rules of ours that point at an executable
    which is gone are cleaned up.

    It only touches rules named "RaCMAN Reloaded (...)", nothing else in the firewall.

    Remove this copy's rules later with:  .\windows-firewall.ps1 -Remove
#>
[CmdletBinding()]
param(
    [switch]$Remove,

    # The executable the rules should name. Defaults to the app beside this script.
    [string]$Program
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Program)) { $Program = Join-Path $PSScriptRoot 'RaCMAN.App.exe' }

# One spelling both sides can agree on: the client compares what it asked for against what the rule
# says, and Windows keeps whatever string it was handed.
$exe = [System.IO.Path]::GetFullPath(
    [System.Environment]::ExpandEnvironmentVariables($Program.Trim().Trim('"').Trim()))

$rules = @(
    @{ Name = 'RaCMAN Reloaded (UDP-In)'; Protocol = 'UDP' },
    @{ Name = 'RaCMAN Reloaded (TCP-In)'; Protocol = 'TCP' }
)

# Re-launch elevated if we are not already administrator. Note this is deliberately not called
# $args: that is an automatic variable, and writing to it is a trap waiting to be sprung.
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltinRole]::Administrator)) {
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-Program', "`"$exe`"")
    if ($Remove) { $argList += '-Remove' }
    try {
        Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argList
    }
    catch {
        Write-Warning 'Administrator approval was declined; no firewall change was made.'
    }
    return
}

# Ours, and about this executable: either it names the same file, or it names one that is no longer
# there. A rule for a different copy of RaCMAN that still exists is left where it is.
function Get-OwnRules {
    foreach ($rule in $rules) {
        foreach ($found in @(Get-NetFirewallRule -DisplayName $rule.Name -ErrorAction SilentlyContinue)) {
            $program = ($found | Get-NetFirewallApplicationFilter -ErrorAction SilentlyContinue).Program
            if ([string]::IsNullOrWhiteSpace($program)) { continue }

            try { $full = [System.IO.Path]::GetFullPath([System.Environment]::ExpandEnvironmentVariables($program)) }
            catch { $full = $program }

            if ($full -ieq $exe -or -not (Test-Path -LiteralPath $full)) { $found }
        }
    }
}

foreach ($stale in @(Get-OwnRules)) {
    Remove-NetFirewallRule -InputObject $stale -ErrorAction SilentlyContinue
}

if ($Remove) {
    Write-Host "Removed the RaCMAN Reloaded firewall rules for $exe."
    Start-Sleep -Seconds 2
    return
}

if (-not (Test-Path -LiteralPath $exe)) {
    Write-Warning "RaCMAN.App.exe was not found ($exe)."
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
