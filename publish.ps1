<#
.SYNOPSIS
    Builds RaCMAN Reloaded and assembles the release folder and zip beside the repos.

.DESCRIPTION
    Publishes src/RaCMAN.App framework-dependent for win-x64 (and, with -All, for linux-x64 and
    osx-x64 as well), then stages ..\build\RaCMAN-Reloaded\ with the published app, the
    controller skins, the moby layout data, the mod library from this repo's mods\ folder,
    ..\qwark\dist\qwark.sprx and ..\qwark\dist\qwark-rpcs3.exe (the RPCS3 helper, win-* only),
    and zips it to ..\build\RaCMAN-Reloaded.zip.

    The console side comes from qwark's committed dist\ folder, which `make dist` fills after a
    build, so a release can be cut without a PS3 SDK on the machine.

    Each runtime gets its own complete folder and zip: the app finds controllerskins\, data\,
    mods\ and qwark.sprx beside its own executable, so the payload cannot be shared between
    runtimes. win-x64 is the one that lands in RaCMAN-Reloaded\; -All adds
    RaCMAN-Reloaded-linux-x64\ and RaCMAN-Reloaded-osx-x64\.

    Nothing is written inside the repo except the usual bin\ and obj\ folders.

.PARAMETER All
    Also publish and package linux-x64 and osx-x64.

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -All
#>
[CmdletBinding()]
param(
    [switch]$All,
    [string]$Configuration = 'Release',
    [string]$OutputRoot,
    [string]$ModsSource,
    [string]$SprxPath,
    [string]$Rpcs3Path
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot
if (-not $OutputRoot) { $OutputRoot = Join-Path $repo '..\build' }
if (-not $ModsSource) { $ModsSource = Join-Path $repo 'mods' }
if (-not $SprxPath)   { $SprxPath   = Join-Path $repo '..\qwark\dist\qwark.sprx' }
if (-not $Rpcs3Path)  { $Rpcs3Path  = Join-Path $repo '..\qwark\dist\qwark-rpcs3.exe' }

# The mod library the client ships with: one folder per title plus the shared Lua helpers. It
# lives in this repo now, so a release carries whatever is committed and nothing else.
$ExpectedMods = @('NPEA00385', 'NPEA00386', 'NPEA00387', 'NPEA00423', 'libs')

function Resolve-Full([string]$path) {
    if ([System.IO.Path]::IsPathRooted($path)) { return [System.IO.Path]::GetFullPath($path) }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $path))
}

function Publish-Rid([string]$rid) {
    $out = Join-Path $repo "src\RaCMAN.App\bin\publish\$rid"
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }

    Write-Host "publish: $rid" -ForegroundColor Cyan
    # Out-Host, not the pipeline: this function's return value is the output directory.
    & dotnet publish (Join-Path $repo 'src\RaCMAN.App') `
        -c $Configuration -r $rid --self-contained false -o $out | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $rid (exit $LASTEXITCODE)" }

    return $out
}

<#
    Copies one mod folder out of this repo's mods\ library.
#>
function Copy-ModFolder([string]$name, [string]$destinationRoot) {
    $source = Join-Path $ModsSource $name
    if (-not (Test-Path $source)) {
        Write-Warning "mods: $name is missing from $ModsSource"
        return $null
    }

    Copy-Item -Recurse -Force $source (Join-Path $destinationRoot $name)
    $count = (Get-ChildItem -Path $source -Recurse -File).Count
    return "$count files"
}

function New-Stage([string]$rid, [string]$publishDir, [string]$stageDir) {
    if (Test-Path $stageDir) { Remove-Item -Recurse -Force $stageDir }
    New-Item -ItemType Directory -Force $stageDir | Out-Null

    Copy-Item -Recurse -Force (Join-Path $publishDir '*') $stageDir

    # Controller skins for the input display.
    Copy-Item -Recurse -Force (Join-Path $repo 'controllerskins') (Join-Path $stageDir 'controllerskins')

    # data\ is already in the publish output through the csproj, but copy it again so a change
    # to that item group cannot silently drop it from a release.
    Copy-Item -Recurse -Force (Join-Path $repo 'src\RaCMAN.App\data') (Join-Path $stageDir 'data')

    # Windows firewall helper (adds an inbound UDP allow rule so the console's telemetry arrives).
    if ($rid -like 'win-*') {
        foreach ($file in @('windows-firewall.ps1', 'Allow through Firewall.cmd')) {
            $src = Join-Path $repo (Join-Path 'packaging' $file)
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
    foreach ($name in $ExpectedMods) {
        $from = Copy-ModFolder $name $modsDir
        if ($from) { Write-Host "  mods\$name  ($from)" }
    }

    Remove-LuaMods $modsDir
}

<#
    Drops any mod whose patch.txt has an "automation:" line. Those mods drive a Lua script, and
    Lua is not in this release, so they would not work: better to ship without them than to ship
    a checkbox that does nothing.

    The in-repo library already leaves them out, so this normally finds nothing; it stays as the
    catch for one being copied in later.
#>
function Remove-LuaMods([string]$modsDir) {
    Get-ChildItem -Path $modsDir -Recurse -Filter 'patch.txt' -File -ErrorAction SilentlyContinue | ForEach-Object {
        if (Select-String -Path $_.FullName -Pattern '^\s*automation\s*:' -Quiet) {
            $modDir = $_.Directory.FullName
            $label = (Split-Path -Leaf (Split-Path -Parent $modDir)) + '\' + (Split-Path -Leaf $modDir)
            Remove-Item -Recurse -Force $modDir
            Write-Host "  mods: excluded $label (needs Lua, which this release does not run)" -ForegroundColor Yellow
        }
    }
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

$runtimes = @(@{ Rid = 'win-x64'; Name = 'RaCMAN-Reloaded' })
if ($All) {
    $runtimes += @{ Rid = 'linux-x64'; Name = 'RaCMAN-Reloaded-linux-x64' }
    $runtimes += @{ Rid = 'osx-x64';   Name = 'RaCMAN-Reloaded-osx-x64' }
}

foreach ($runtime in $runtimes) {
    $publishDir = Publish-Rid $runtime.Rid
    $stageDir = Join-Path $OutputRoot $runtime.Name

    Write-Host "stage: $stageDir" -ForegroundColor Cyan
    New-Stage $runtime.Rid $publishDir $stageDir
    New-Zip $stageDir (Join-Path $OutputRoot ($runtime.Name + '.zip'))

    Write-Host "top level of $($runtime.Name):"
    Get-ChildItem $stageDir | Sort-Object PSIsContainer, Name | ForEach-Object {
        if ($_.PSIsContainer) { Write-Host ("  {0}\" -f $_.Name) }
        else { Write-Host ("  {0}  ({1:N0} bytes)" -f $_.Name, $_.Length) }
    }
}
