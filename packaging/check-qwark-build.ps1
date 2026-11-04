<#
.SYNOPSIS
    Refuses a qwark dist\ folder that is not a whole release: a test build, a game module short, or a
    ui.pak without Tools of Destruction's entries.

.DESCRIPTION
    qwark's `make` builds a test module unless it is told otherwise, and writes qwark.build beside
    it: `build N`, `dev 0|1` and `id 0x...` (revision 1.24, qwark's docs/PROTOCOL.md 3.1.1). A test
    module sets SessionInfo flags2 bit4 DEV, and every client replaces a DEV module whose build ID
    is not its own, so a release that carried one would be replaced by the next client build of
    any kind. A release is made with `make release` in qwark, which builds the module without the
    bit and fills dist\ from it.

    Since qwark build 57 a release ships everything its test builds had: its games are modules of
    their own (revision 1.26, PROTOCOL.md 9.11), rac1.sprx to rac4.sprx and tod.sprx beside
    qwark.sprx (the list is game-module-names.ps1), and its ui.pak carries Tools of Destruction's
    Lua helper (pak.py --tod, PROTOCOL.md 9.10: entries of kinds 3 to 6 for game 5), without which
    the module refuses the pak as another build's.

    This script stops when dist\qwark.build is missing, unreadable, says `dev 1`, or names another
    build than qwark's src\core\proto.h; when any of the five game modules is missing or is no
    signed SELF; when dist\ holds the first test builds' game-*.sprx (revision 1.26), which no
    module reads; and when dist\ui.pak is missing, is no format 2 pak of the module's build, or
    lacks ToD's four entries. release.ps1 runs it before it tags anything, and the release workflow
    runs it on the qwark it checked out. It writes one line when the dist\ is a release, and throws
    with what to do otherwise.

.PARAMETER Dist
    qwark's dist\ folder.

.PARAMETER ProtoH
    qwark's src\core\proto.h, for QWARK_BUILD. Optional: the workflow has the whole repository,
    and passes it; without it the build number is not compared.

.EXAMPLE
    .\packaging\check-qwark-build.ps1 -Dist ..\qwark\dist -ProtoH ..\qwark\src\core\proto.h
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Dist,
    [string]$ProtoH
)

$ErrorActionPreference = 'Stop'

$file = [System.IO.Path]::Combine($Dist, 'qwark.build')
$fix = 'Build a release in qwark with `make release` (or `_Make.bat release`) and commit dist\.'

if (-not (Test-Path $file)) {
    throw "qwark has no dist\qwark.build, so whether its module is a release cannot be told. $fix"
}

$fields = @{}
foreach ($line in (Get-Content -Path $file)) {
    $line = $line.Trim()
    if ($line.Length -eq 0 -or $line.StartsWith('#')) { continue }
    $parts = $line -split '\s+', 2
    if ($parts.Count -eq 2) { $fields[$parts[0]] = $parts[1] }
}

if ($fields['build'] -notmatch '^\d+$' -or $fields['dev'] -notmatch '^[01]$' -or $fields['id'] -notmatch '^0x[0-9A-Fa-f]{8}$') {
    throw "qwark's dist\qwark.build does not say build, dev and id. $fix"
}

if ($fields['dev'] -eq '1') {
    throw ("qwark's dist\ holds a TEST build (build $($fields['build']), id $($fields['id'])): every client " +
           "would replace it. $fix")
}

if ($ProtoH) {
    $proto = Get-Content -Path $ProtoH -Raw
    if ($proto -notmatch '#define\s+QWARK_BUILD\s+(\d+)') { throw "Could not read QWARK_BUILD from $ProtoH." }
    if ([int]$Matches[1] -ne [int]$fields['build']) {
        throw ("qwark's dist\ module is build $($fields['build']) and its sources are build $($Matches[1]). $fix")
    }
}

# Revision 1.26, and in a release since build 57: the games are rac1.sprx to rac4.sprx and tod.sprx beside the
# module (game-module-names.ps1). Every one must be there, and be a signed SELF ("SCE\0"), as the client checks
# before it sends one; a release without one would leave that game with no features on every console.
. (Join-Path $PSScriptRoot 'game-module-names.ps1')
$missing = @($GameModuleNames | Where-Object { -not (Test-Path ([System.IO.Path]::Combine($Dist, $_))) })
if ($missing.Count -gt 0) {
    throw ("qwark's dist\ holds a release module without $($missing.Count) of its $($GameModuleNames.Count) game " +
           "module(s) ($($missing -join ', ')). A release ships all of them since build 57. $fix")
}
foreach ($name in $GameModuleNames) {
    $bytes = [System.IO.File]::ReadAllBytes([System.IO.Path]::Combine($Dist, $name))
    if ($bytes.Length -le 64 -or $bytes[0] -ne 0x53 -or $bytes[1] -ne 0x43 -or $bytes[2] -ne 0x45 -or $bytes[3] -ne 0) {
        throw "qwark's dist\$name is no signed module ($($bytes.Length) bytes, no SELF header). $fix"
    }
}

# The first test builds of revision 1.26 named them game-<n>.sprx, which no module reads.
$legacy = @(Get-ChildItem -Path $Dist -Filter $LegacyGameModulePattern -File -ErrorAction SilentlyContinue)
if ($legacy.Count -gt 0) {
    throw ("qwark's dist\ holds $($legacy.Count) game module(s) under an old name " +
           "($(($legacy | ForEach-Object { $_.Name }) -join ', ')), which no module reads; delete them from dist\. $fix")
}

# ui.pak (PROTOCOL.md 9.10): "QPAK", format 2, the module's build, and, since build 57 in a release too, Tools of
# Destruction's Lua helper: entries of kind 3 (its code) and 4 to 6 (its three Lua chunks) for game 5. A module
# with ToD refuses a pak without them as another build's, which would leave the console menu and the savefile
# helpers out of every game.
$pakPath = [System.IO.Path]::Combine($Dist, 'ui.pak')
if (-not (Test-Path $pakPath)) { throw "qwark has no dist\ui.pak. $fix" }
$pak = [System.IO.File]::ReadAllBytes($pakPath)
function Read-Be32([byte[]]$b, [int]$at) {
    return ([uint32]$b[$at] -shl 24) -bor ([uint32]$b[$at + 1] -shl 16) -bor ([uint32]$b[$at + 2] -shl 8) -bor [uint32]$b[$at + 3]
}
if ($pak.Length -lt 32 -or [System.Text.Encoding]::ASCII.GetString($pak, 0, 4) -ne 'QPAK' -or
    $pak[4] -ne 0 -or $pak[5] -ne 2) {
    throw "qwark's dist\ui.pak is no format 2 pak. $fix"
}
$pakBuild = Read-Be32 $pak 8
if ($pakBuild -ne [uint32]$fields['build']) {
    throw "qwark's dist\ui.pak is build $pakBuild's and its module build $($fields['build']). $fix"
}
$entries = [int]$pak[20]
if ($pak.Length -lt 32 + 32 * $entries) { throw "qwark's dist\ui.pak is cut short. $fix" }
$todKinds = @{}
for ($i = 0; $i -lt $entries; $i++) {
    $at = 32 + 32 * $i
    if ($pak[$at] -eq 5 -and $pak[$at + 1] -ge 3 -and $pak[$at + 1] -le 6) { $todKinds[[int]$pak[$at + 1]] = $true }
}
if ($todKinds.Count -ne 4) {
    throw ("qwark's dist\ui.pak has no Tools of Destruction Lua helper (pak.py --tod), so a build 57 module " +
           "would refuse it as another build's. $fix")
}

Write-Host ("  dist\ holds a release: build $($fields['build']), id $($fields['id']), $($GameModuleNames.Count) game " +
            "modules, ui.pak with ToD's Lua helper") -ForegroundColor Green
