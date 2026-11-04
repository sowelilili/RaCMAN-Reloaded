<#
.SYNOPSIS
    Copies a qwark dist's game modules beside the staged qwark.sprx, a release's and a test build's.

.DESCRIPTION
    Since revision 1.26 qwark's core carries no game: each is an SPRX of its own, named for its
    game, rac1.sprx (RaC1) to rac4.sprx (Deadlocked) and tod.sprx (ToD; the list is
    game-module-names.ps1), which `make dist` puts in dist\ beside qwark.sprx, ui.pak and
    qwark.build (qwark's docs/PROTOCOL.md 9.11). Since qwark build 57 a release ships them too, as
    its test builds always did. The client sends the modules it finds beside its own qwark.sprx,
    exactly as it sends ui.pak.

    This script removes any module already in -Destination, and any game-*.sprx the first test
    builds of revision 1.26 named them, then copies each one game-module-names.ps1 lists from
    -Source. A release (-BuildInfo, the qwark.build beside the module, says `dev 0`) must have all
    of them, and the script stops when one is missing, as check-qwark-build.ps1 does: a release
    without one would leave that game with no features. A test build, or a dist\ with no
    qwark.build, copies whichever there are and warns about the rest. publish.ps1 calls it for
    every runtime it stages.

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
$missing = @($GameModuleNames | Where-Object { -not (Test-Path ([System.IO.Path]::Combine($Source, $_))) })

$known = Test-Path $BuildInfo
$release = $known -and (Select-String -Path $BuildInfo -Pattern '^dev 0' -Quiet)

if ($release -and $missing.Count -gt 0) {
    throw ("game modules: $Source holds a release module without $($missing -join ', '); a release ships all " +
           "$($GameModuleNames.Count) since qwark build 57. Build one in qwark with ``make release``.")
}

if ($modules.Count -eq 0) {
    Write-Warning "game modules: none in $Source; every game will have no game features"
    return
}

foreach ($module in $modules) {
    Copy-Item -Force $module (Join-Path $Destination ([System.IO.Path]::GetFileName($module)))
    Write-Host "  $([System.IO.Path]::GetFileName($module)): $([System.IO.Path]::GetFullPath($module))"
}

if ($missing.Count -gt 0) {
    Write-Warning ("game modules: only $($modules.Count) of $($GameModuleNames.Count) in $Source; the games without " +
                   'one will have no game features')
}
