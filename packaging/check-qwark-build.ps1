<#
.SYNOPSIS
    Refuses a qwark dist\ folder whose module is a test build.

.DESCRIPTION
    qwark's `make` builds a test module unless it is told otherwise, and writes qwark.build beside
    it: `build N`, `dev 0|1` and `id 0x...` (revision 1.24, qwark's docs/PROTOCOL.md 3.1.1). A test
    module sets SessionInfo flags2 bit4 DEV, and every client replaces a DEV module whose build ID
    is not its own, so a release that carried one would be replaced by the next client build of
    any kind. A release is made with `make release` in qwark, which builds the module without the
    bit and fills dist\ from it.

    This script stops when dist\qwark.build is missing, unreadable, says `dev 1`, or names another
    build than qwark's src\core\proto.h, and when dist\ holds game modules (game-*.sprx, revision
    1.26): a release links its games in, so any there are a test build's, left behind. release.ps1
    runs it before it tags anything, and the release workflow runs it on the qwark it checked out.
    It writes one line when the dist\ is a release, and throws with what to do otherwise.

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
$fix = 'Build a release module in qwark with `make release` (or `_Make.bat release`) and commit dist\.'

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

# Revision 1.26: a test build's games are game-1.sprx to game-4.sprx beside it. A release links its
# games in, so a release dist\ with any is one a test build's `make dist` left them in.
$modules = @(Get-ChildItem -Path $Dist -Filter 'game-*.sprx' -File -ErrorAction SilentlyContinue)
if ($modules.Count -gt 0) {
    throw ("qwark's dist\ holds a release module and $($modules.Count) game module(s) from a test build " +
           "($(($modules | ForEach-Object { $_.Name }) -join ', ')). A release links its games in; delete them from dist\. $fix")
}

if ($ProtoH) {
    $proto = Get-Content -Path $ProtoH -Raw
    if ($proto -notmatch '#define\s+QWARK_BUILD\s+(\d+)') { throw "Could not read QWARK_BUILD from $ProtoH." }
    if ([int]$Matches[1] -ne [int]$fields['build']) {
        throw ("qwark's dist\ module is build $($fields['build']) and its sources are build $($Matches[1]). $fix")
    }
}

Write-Host "  dist\ holds a release module: build $($fields['build']), id $($fields['id'])" -ForegroundColor Green
