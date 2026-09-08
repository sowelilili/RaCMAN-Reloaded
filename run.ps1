<#
.SYNOPSIS
    Build RaCMAN Reloaded with all its assets and run it.

.DESCRIPTION
    Stages a full copy (the app, the controller skins, the moby layout data, the mod library and
    qwark.sprx, plus the Windows firewall helper) into .\run\ exactly the way the release is built,
    then launches it. Everything the input display, mods and firewall need is therefore beside the
    executable, so a run behaves like the shipped build rather than a bare "dotnet run".

    This does not rebuild qwark.sprx; it copies the one from ..\qwark. Rebuild that separately (see
    ..\qwark\CLAUDE.md) if you changed the PS3 module.

    Anything after -Configuration is passed straight to the app.

.EXAMPLE
    .\run.ps1
    .\run.ps1 --connect 192.168.1.50
    .\run.ps1 -Configuration Debug
#>

# Deliberately a simple script (no CmdletBinding): that lets the app's GNU-style "--flag" arguments
# fall through into $args instead of being rejected as unknown parameters.
param([string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$runRoot = Join-Path $here 'run'

# A previous launch from .\run\ locks its own files, which would fail the rebuild. Stop only those
# instances (started from this run\ folder), never a copy the user is running from anywhere else.
$runFull = [System.IO.Path]::GetFullPath($runRoot)
Get-Process -Name 'RaCMAN.App' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($runFull, [System.StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force -ErrorAction SilentlyContinue

& (Join-Path $here 'publish.ps1') -Configuration $Configuration -OutputRoot $runRoot | Out-Host
if ($LASTEXITCODE -ne 0) { throw "Build failed (exit $LASTEXITCODE)" }

$exe = Join-Path $runRoot 'RaCMAN-Reloaded\RaCMAN.App.exe'
if (-not (Test-Path $exe)) { throw "Build did not produce $exe" }

Write-Host ''
Write-Host "Launching $exe" -ForegroundColor Green
if ($args.Count -gt 0) { & $exe @args } else { & $exe }
