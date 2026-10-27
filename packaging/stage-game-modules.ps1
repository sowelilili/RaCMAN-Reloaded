<#
.SYNOPSIS
    Copies a qwark test build's game modules beside the staged qwark.sprx, and nothing for a release.

.DESCRIPTION
    Since revision 1.26 a qwark test build carries no game: each is an SPRX of its own, named for
    its game, rac1.sprx (RaC1) to rac4.sprx (Deadlocked; the list is game-module-names.ps1), which
    `make dist` puts in dist\ beside
    qwark.sprx, ui.pak and qwark.build (qwark's docs/PROTOCOL.md 9.11). A release links its games
    in, so its dist\ has none. The client sends the modules it finds beside its own qwark.sprx,
    exactly as it sends ui.pak.

    This script removes any module already in -Destination, and any game-*.sprx the first test
    builds of revision 1.26 named them, then copies rac1.sprx to rac4.sprx from -Source when
    -BuildInfo (the qwark.build beside the module) says `dev 1`. A
    source with none of them copies none and is not an error. A release (`dev 0`), or a dist\ with
    no qwark.build, copies none even when some are there, and warns: those are a test build's,
    left behind, and a release must never put them on a console. publish.ps1 calls it for every
    runtime it stages.

.PARAMETER Source
    The folder the modules are in: qwark's dist\, beside qwark.sprx.

.PARAMETER BuildInfo
    The qwark.build that describes the qwark.sprx being staged.

.PARAMETER Destination
    The staged application folder, where qwark.sprx goes.

.EXAMPLE
    .\packaging\stage-game-modules.ps1 -Source ..\qwark\dist -BuildInfo ..\qwark\dist\qwark.build -Destination ..\build\RaCMAN-Reloaded
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Source,
    [Parameter(Mandatory = $true)][string]$BuildInfo,
    [Parameter(Mandatory = $true)][string]$Destination
)

$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'game-module-names.ps1')

# Whatever an earlier stage left, under either name: only the modules of the dist being staged go out.
foreach ($name in $GameModuleNames) {
    $old = Join-Path $Destination $name
    if (Test-Path $old) { Remove-Item -Force $old }
}
Get-ChildItem -Path $Destination -Filter $LegacyGameModulePattern -File -ErrorAction SilentlyContinue |
    Remove-Item -Force

$modules = @($GameModuleNames | ForEach-Object { [System.IO.Path]::Combine($Source, $_) } | Where-Object { Test-Path $_ })

$dev = (Test-Path $BuildInfo) -and (Select-String -Path $BuildInfo -Pattern '^dev 1' -Quiet)

if (-not $dev) {
    if ($modules.Count -gt 0) {
        $names = ($modules | ForEach-Object { [System.IO.Path]::GetFileName($_) }) -join ', '
        Write-Warning ("game modules: $Source holds $names, but its module is not a test build; a release links " +
                       'its games in, so they were not staged')
    }
    else {
        Write-Host '  game modules: none (a release links its games in)'
    }
    return
}

if ($modules.Count -eq 0) {
    Write-Host "  game modules: none in $Source"
    return
}

foreach ($module in $modules) {
    Copy-Item -Force $module (Join-Path $Destination ([System.IO.Path]::GetFileName($module)))
    Write-Host "  $([System.IO.Path]::GetFileName($module)): $([System.IO.Path]::GetFullPath($module))"
}

if ($modules.Count -lt $GameModuleNames.Count) {
    Write-Warning ("game modules: only $($modules.Count) of $($GameModuleNames.Count) in $Source; the games without " +
                   'one will have no game features')
}
