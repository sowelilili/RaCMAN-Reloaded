<#
.SYNOPSIS
    Builds RaCMAN Reloaded and assembles the release folder and zip beside the repos.

.DESCRIPTION
    Publishes src/RaCMAN.App framework-dependent for win-x64 (and, with -All, for linux-x64 and
    osx-x64 as well), then stages ..\build\RaCMAN-Reloaded\ with the published app, the
    controller skins, the moby layout data, ..\qwark\qwark.sprx when it has been built,
    ..\qwark\qwark-rpcs3.exe (the RPCS3 helper, win-* only) when it has been built, and the
    mod library, and zips it to ..\build\RaCMAN-Reloaded.zip.

    Each runtime gets its own complete folder and zip: the app finds controllerskins\, data\,
    mods\ and qwark.sprx beside its own executable, so the payload cannot be shared between
    runtimes. win-x64 is the one that lands in RaCMAN-Reloaded\; -All adds
    RaCMAN-Reloaded-linux-x64\ and RaCMAN-Reloaded-osx-x64\.

    The release workflow calls this script too, with -Rid, -Version, -SelfContained and -NoZip,
    and hands it the payload paths from the repositories it checked out, so there is one
    description of the release layout rather than two. It therefore runs under PowerShell 7 on a
    Linux runner as well as under Windows PowerShell 5.1 here, which is why every path is built
    with [System.IO.Path]::Combine rather than with backslashes.

    Nothing is written inside the repo except the usual bin\ and obj\ folders.

.PARAMETER All
    Also publish and package linux-x64 and osx-x64.

.PARAMETER Rid
    Publish only this runtime, whatever -All says. The release workflow's one job per platform.

.PARAMETER Version
    The version to stamp the assembly with, without a leading "v". The updater reads it back out
    of the assembly, so a release must pass it; a local build leaves the csproj's own.

.PARAMETER SelfContained
    Publish the .NET runtime with the app. What the releases do, so an update never depends on
    what is installed on the machine.

.PARAMETER NoZip
    Stage the folder and stop. The release workflow packages the staged folder itself.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -All
    .\publish.ps1 -Rid linux-x64 -Version 1.2.0 -SelfContained -NoZip -StageName RaCMAN-Reloaded
#>
[CmdletBinding()]
param(
    [switch]$All,
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$ModsSource,
    [string]$SprxPath,
    [string]$Rpcs3Path,
    [string]$Rid,
    [string]$StageName,
    [string]$Version,
    [switch]$SelfContained,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot

function Combine {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Parts)
    return [System.IO.Path]::Combine([string[]]$Parts)
}

if (-not $OutputRoot) { $OutputRoot = Combine $repo '..' 'build' }
if (-not $ModsSource) { $ModsSource = Combine $repo '..' '..' 'legacy' 'racman-official' 'mods' }
if (-not $SprxPath)   { $SprxPath   = Combine $repo '..' 'qwark' 'qwark.sprx' }
if (-not $Rpcs3Path)  { $Rpcs3Path  = Combine $repo '..' 'qwark' 'qwark-rpcs3.exe' }

# The mod library the client ships with: one folder per title plus the shared Lua helpers.
$ExpectedMods = @('NPEA00385', 'NPEA00386', 'NPEA00387', 'NPEA00423', 'libs')

# What the client reads to tell a shipped mod from one the user added, when it moves an older
# installation's files into the data folder. See DataFolderMigration.
$ShippedManifest = 'shipped.txt'

function Resolve-Full([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $path))
}

function Publish-Rid([string]$rid) {
    $out = Combine $repo 'src' 'RaCMAN.App' 'bin' 'publish' $rid
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    Write-Host "publish: $rid" -ForegroundColor Cyan

    $arguments = @(
        'publish', (Combine $repo 'src' 'RaCMAN.App'),
        '-c', $Configuration,
        '-r', $rid,
        '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }),
        '-o', $out
    )

    # The updater shows this, and the release names it from the tag; a build with no -Version
    # keeps whatever the csproj says, which is what a local run wants.
    if ($Version) { $arguments += "-p:Version=$Version" }

    # Out-Host, not the pipeline: this function's return value is the output directory.
    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }

    return $out
}

<#
    Copies one mod folder out of racman-official. When it is missing from that repo's working
    tree, which happens on branches that do not carry the whole library, it is recovered from
    the repo's master branch instead. That is a read-only git operation on the sibling repo.
#>
function Copy-ModFolder([string]$name, [string]$destinationRoot) {
    $source = Combine $ModsSource $name
    if (Test-Path $source) {
        Copy-Item -Recurse -Force $source (Join-Path $destinationRoot $name)
        return "working tree"
    }

    $repoRoot = Split-Path -Parent $ModsSource
    if (-not (Test-Path (Join-Path $repoRoot '.git'))) {
        Write-Warning "mods: $name is missing from $ModsSource and there is no git repo to recover it from"
        return $null
    }

    $temp = Combine ([System.IO.Path]::GetTempPath()) ("racman-mods-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force $temp | Out-Null
    $archive = Join-Path $temp 'mods.zip'

    try {
        & git -C $repoRoot archive --format=zip -o $archive master ("mods/" + $name) 2>$null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path $archive)) {
            Write-Warning "mods: $name is missing from $ModsSource and is not on that repo's master branch either"
            return $null
        }

        Expand-Archive -Path $archive -DestinationPath $temp -Force
        Copy-Item -Recurse -Force (Combine $temp 'mods' $name) (Join-Path $destinationRoot $name)
        Write-Warning "mods: $name is not in $ModsSource on the checked-out branch; took it from that repo's master"
        return "master"
    }
    finally {
        Remove-Item -Recurse -Force $temp -ErrorAction SilentlyContinue
    }
}

function New-Stage([string]$rid, [string]$publishDir, [string]$stageDir) {
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    New-Item -ItemType Directory -Force $stageDir | Out-Null

    Copy-Item -Recurse -Force (Join-Path $publishDir '*') $stageDir

    # Controller skins for the input display.
    Copy-Item -Recurse -Force (Combine $repo 'controllerskins') (Join-Path $stageDir 'controllerskins')

    # data\ is already in the publish output through the csproj, but copy it again so a change
    # to that item group cannot silently drop it from a release.
    Copy-Item -Recurse -Force (Combine $repo 'src' 'RaCMAN.App' 'data') (Join-Path $stageDir 'data')

    # Windows firewall helper (adds an inbound UDP allow rule so the console's telemetry arrives).
    if ($rid -like 'win-*') {
        foreach ($file in @('windows-firewall.ps1', 'Allow through Firewall.cmd')) {
            $src = Combine $repo 'packaging' $file
            if (Test-Path $src) { Copy-Item -Force $src (Join-Path $stageDir $file) }
        }
    }

    if (Test-Path $SprxPath) {
        Copy-Item -Force $SprxPath (Join-Path $stageDir 'qwark.sprx')
        Write-Host "  qwark.sprx: $(Resolve-Full $SprxPath)"
    }
    else {
        Write-Warning "qwark.sprx not found at $SprxPath; the release will not carry the module"
    }

    # The RPCS3 helper: qwark's core built for the PC, which the client starts beside itself when
    # the target is RPCS3. A Windows executable for now, so it only goes into the win-* payloads.
    if ($rid -like 'win-*') {
        if (Test-Path $Rpcs3Path) {
            Copy-Item -Force $Rpcs3Path (Join-Path $stageDir 'qwark-rpcs3.exe')
            Write-Host "  qwark-rpcs3.exe: $(Resolve-Full $Rpcs3Path)"
        }
        else {
            Write-Warning "qwark-rpcs3.exe not found at $Rpcs3Path; the release will not carry the RPCS3 helper"
        }
    }

    $modsDir = Join-Path $stageDir 'mods'
    New-Item -ItemType Directory -Force $modsDir | Out-Null

    if (Test-Path $ModsSource) {
        foreach ($name in $ExpectedMods) {
            $from = Copy-ModFolder $name $modsDir
            if ($from) { Write-Host "  mods\$name  ($from)" }
        }
    }
    else {
        Write-Warning "mods: $ModsSource does not exist; the release will not carry the shipped mod library"
    }

    Remove-LuaMods $modsDir
    Write-ShippedManifest $modsDir
}

<#
    Drops any mod whose patch.txt has an "automation:" line. Those mods drive a Lua script, and
    Lua is not in this release, so they would not work: better to ship without them than to ship
    a checkbox that does nothing.
#>
function Remove-LuaMods([string]$modsDir) {
    Get-ChildItem -Path $modsDir -Recurse -Filter 'patch.txt' -File -ErrorAction SilentlyContinue | ForEach-Object {
        if (Select-String -Path $_.FullName -Pattern '^\s*automation\s*:' -Quiet) {
            $modDir = $_.Directory.FullName
            $label = (Split-Path -Leaf (Split-Path -Parent $modDir)) + '/' + (Split-Path -Leaf $modDir)
            Remove-Item -Recurse -Force $modDir
            Write-Host "  mods: excluded $label (needs Lua, which this release does not run)" -ForegroundColor Yellow
        }
    }
}

<#
    Writes mods\shipped.txt: every title folder and every mod folder in it, as the client's
    migration reads them. Once the mods a user installed themselves live in the data folder, this
    list is the only way to tell an old installation's mods apart from the ones a release put
    beside them, and a folder with no list is treated as a development tree whose mods are the
    repo's.
#>
function Write-ShippedManifest([string]$modsDir) {
    $entries = New-Object System.Collections.Generic.List[string]
    foreach ($title in (Get-ChildItem -Path $modsDir -Directory | Sort-Object Name)) {
        $entries.Add($title.Name)
        foreach ($mod in (Get-ChildItem -Path $title.FullName -Directory | Sort-Object Name)) {
            $entries.Add($title.Name + '/' + $mod.Name)
        }
    }

    $lines = @(
        '# The mod folders this release shipped, one per line, relative to this folder.',
        '# RaCMAN Reloaded reads it to tell them from the mods you installed yourself.'
    ) + $entries

    Set-Content -Path (Join-Path $modsDir $ShippedManifest) -Value $lines -Encoding UTF8
    Write-Host "  mods/$ShippedManifest  ($($entries.Count) entries)"
}

function New-Zip([string]$stageDir, [string]$zipPath) {
    if (Test-Path $zipPath) { Remove-Item -Force $zipPath }
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
    $size = (Get-Item $zipPath).Length
    Write-Host ("zip: {0} ({1:N0} bytes, {2:N1} MB)" -f $zipPath, $size, ($size / 1MB)) -ForegroundColor Green
}

# ---------------------------------------------------------------------------- run

New-Item -ItemType Directory -Force $OutputRoot | Out-Null
$OutputRoot = Resolve-Full $OutputRoot

if ($Rid) {
    $name = if ($StageName) { $StageName } elseif ($Rid -eq 'win-x64') { 'RaCMAN-Reloaded' } else { "RaCMAN-Reloaded-$Rid" }
    $runtimes = @(@{ Rid = $Rid; Name = $name })
}
else {
    $runtimes = @(@{ Rid = 'win-x64'; Name = 'RaCMAN-Reloaded' })
    if ($All) {
        $runtimes += @{ Rid = 'linux-x64'; Name = 'RaCMAN-Reloaded-linux-x64' }
        $runtimes += @{ Rid = 'osx-x64';   Name = 'RaCMAN-Reloaded-osx-x64' }
    }
}

foreach ($runtime in $runtimes) {
    $publishDir = Publish-Rid $runtime.Rid
    $stageDir = Join-Path $OutputRoot $runtime.Name

    Write-Host "stage: $stageDir" -ForegroundColor Cyan
    New-Stage $runtime.Rid $publishDir $stageDir
    if (-not $NoZip) { New-Zip $stageDir (Join-Path $OutputRoot ($runtime.Name + '.zip')) }

    Write-Host "top level of $($runtime.Name):"
    Get-ChildItem $stageDir | Sort-Object PSIsContainer, Name | ForEach-Object {
        if ($_.PSIsContainer) { Write-Host ("  {0}/" -f $_.Name) }
        else { Write-Host ("  {0}  ({1:N0} bytes)" -f $_.Name, $_.Length) }
    }
}
