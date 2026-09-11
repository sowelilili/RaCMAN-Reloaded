<#
.SYNOPSIS
    Checks both repositories, then tags and pushes a release.

.DESCRIPTION
    Cutting a release is two tags: one on qwark, so the console module that shipped is pinned, and
    one here, which is what starts .github/workflows/release.yml. Everything else is the workflow's
    job. This script is the checks that come first, in one place, because the ones that matter are
    easy to forget and expensive to get wrong: a tag on an unpushed commit builds nothing, and a
    stale dist\ folder ships last week's SPRX.

    In order, it:

      1. Reads the version and works out the tag name.
      2. Refuses a working tree with uncommitted changes, in either repository.
      3. Refuses a branch that has diverged from the remote, and pushes one that is merely ahead.
      4. Refuses a tag that already exists, locally or on the remote.
      5. Refuses a qwark dist\ folder older than qwark's own sources, or missing a file.
      6. Refuses a client that expects a newer qwark build than qwark carries.
      7. Runs both test suites.
      8. Shows what it is about to do and asks, then tags and pushes qwark and then this repository.

    The client's version comes from the tag, so nothing in either repository has to be edited to
    release. Run it with -WhatIf to see every step without pushing anything.

.PARAMETER Version
    The version to release, with or without a leading "v". The tag is always "v" plus the version.

.PARAMETER QwarkPath
    Where the qwark repository is. Defaults to ..\qwark beside this one.

.PARAMETER Branch
    The branch both repositories release from. Defaults to main.

.PARAMETER Remote
    The remote to push to. Defaults to origin.

.PARAMETER SkipTests
    Do not run the test suites. For a second attempt after a push failed, when nothing has changed.

.PARAMETER Yes
    Do not ask before tagging and pushing.

.EXAMPLE
    .\release.ps1 1.0.0 -WhatIf
    .\release.ps1 1.0.0
    .\release.ps1 v1.0.1 -SkipTests -Yes
#>
[CmdletBinding(SupportsShouldProcess = $true, ConfirmImpact = 'Medium')]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Version,

    [string]$QwarkPath,
    [string]$Branch = 'main',
    [string]$Remote = 'origin',
    [switch]$SkipTests,
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$repo = $PSScriptRoot

function Combine {
    param([Parameter(ValueFromRemainingArguments = $true)][string[]]$Parts)
    return [System.IO.Path]::Combine([string[]]$Parts)
}

# Every external tool goes through here. Under Windows PowerShell, a native command that writes to
# stderr raises an error record whenever the caller has redirected or piped the stream, and with
# $ErrorActionPreference set to Stop that record ends the script. git writes its progress to stderr,
# so a release piped into a log file would die on a push that had in fact worked. The preference
# goes back to Continue for the length of the call and the exit code is the only thing believed.
function Invoke-Native {
    param([scriptblock]$Command)

    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $Command }
    finally { $ErrorActionPreference = $previous }
}

function Invoke-Git {
    param(
        [string]$Repo,
        [string[]]$GitArgs,
        [switch]$AllowFail
    )

    $output = Invoke-Native { & git -C $Repo @GitArgs }
    if (-not $AllowFail -and $LASTEXITCODE -ne 0) {
        throw "git $($GitArgs -join ' ') failed in $Repo (exit $LASTEXITCODE)"
    }

    return $output
}

# A test suite: its output goes to the screen as it runs, and a non-zero exit stops the release.
function Invoke-Suite {
    param([string]$What, [scriptblock]$Command)

    Write-Step "running $What"
    Invoke-Native $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed (exit $LASTEXITCODE)" }
    Write-Good "$What passed"
}

function Write-Step([string]$text) { Write-Host $text -ForegroundColor Cyan }
function Write-Good([string]$text) { Write-Host "  $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "  $text" }

# ------------------------------------------------------------------------------- the version

$clean = $Version.Trim()
if ($clean.StartsWith('v')) { $clean = $clean.Substring(1) }

if ($clean -notmatch '^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$') {
    throw "Version '$Version' is not a version: expected 1.2.3, or 1.2.3-beta.1, with or without a leading v."
}

$tag = "v$clean"

# ------------------------------------------------------------------------------- the repositories

if (-not $QwarkPath) { $QwarkPath = Combine $repo '..' 'qwark' }
$qwark = [System.IO.Path]::GetFullPath($QwarkPath)

if (-not (Test-Path (Combine $qwark '.git'))) {
    throw "No qwark repository at $qwark. Pass -QwarkPath if it lives somewhere else."
}

$repos = @(
    [pscustomobject]@{ Name = 'qwark';           Path = $qwark },
    [pscustomobject]@{ Name = 'RaCMAN Reloaded'; Path = $repo }
)

Write-Step "release $tag"
Write-Note "qwark:  $qwark"
Write-Note "client: $repo"

# ------------------------------------------------------------- clean trees, and branches in step

# Filled in below and acted on at the end, so the summary can say what the push will do.
$toPush = @()

foreach ($r in $repos) {
    Write-Step "checking $($r.Name)"

    $dirty = Invoke-Git $r.Path @('status', '--porcelain')
    if ($dirty) {
        throw "$($r.Name) has uncommitted changes. Commit or stash them first:`n$($dirty -join "`n")"
    }
    Write-Good 'the working tree is clean'

    $head = (Invoke-Git $r.Path @('rev-parse', '--abbrev-ref', 'HEAD')).Trim()
    if ($head -ne $Branch) {
        throw "$($r.Name) is on '$head', not '$Branch'. Switch to $Branch, or pass -Branch."
    }

    Invoke-Git $r.Path @('fetch', '--quiet', '--tags', $Remote) | Out-Null

    # Not $local and $remote: PowerShell does not care about case, so $remote would quietly become
    # the commit this script then tries to push to.
    $localHead = (Invoke-Git $r.Path @('rev-parse', 'HEAD')).Trim()
    $remoteHead = (Invoke-Git $r.Path @('rev-parse', "$Remote/$Branch")).Trim()

    if ($localHead -ne $remoteHead) {
        $behind = (Invoke-Git $r.Path @('rev-list', '--count', "$localHead..$remoteHead")).Trim()
        if ($behind -ne '0') {
            throw "$($r.Name) is $behind commit(s) behind $Remote/$Branch. Pull before releasing."
        }

        $ahead = (Invoke-Git $r.Path @('rev-list', '--count', "$remoteHead..$localHead")).Trim()
        $r | Add-Member -NotePropertyName Ahead -NotePropertyValue ([int]$ahead)
        $toPush += $r
        Write-Note "$ahead commit(s) to push to $Remote/$Branch"
    }
    else {
        Write-Good "$Remote/$Branch is up to date"
    }

    # A tag that exists already is either a release that happened or a mistake. Either way this
    # script must not move it: the workflow ran against whatever it pointed at.
    $localTag = Invoke-Git $r.Path @('tag', '--list', $tag)
    if ($localTag) { throw "$($r.Name) already has the tag $tag locally." }

    $remoteTag = Invoke-Git $r.Path @('ls-remote', '--tags', $Remote, "refs/tags/$tag")
    if ($remoteTag) { throw "$($r.Name) already has the tag $tag on $Remote." }

    Write-Good "$tag is free"
}

# ------------------------------------------------------------------ what the workflow will collect

Write-Step 'checking the console payload'

foreach ($file in @('qwark.sprx', 'qwark-rpcs3.exe')) {
    $path = Combine $qwark 'dist' $file
    if (-not (Test-Path $path)) {
        throw "qwark has no dist\$file. Build it in qwark (make, then make dist) and commit it."
    }
}

# The release takes the committed binaries, so a source file newer than them means the module in
# dist\ is not the module in src\. Only this machine's timestamps can tell; the workflow cannot.
$sprx = Get-Item (Combine $qwark 'dist' 'qwark.sprx')
$newest = Get-ChildItem -Path (Combine $qwark 'src') -Recurse -File |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($newest -and $newest.LastWriteTime -gt $sprx.LastWriteTime) {
    throw ("qwark's dist\qwark.sprx is older than $($newest.Name). Rebuild it in qwark " +
           '(make, then make dist) and commit it, or the release ships the previous module.')
}

Write-Good "dist\qwark.sprx is newer than every source ($('{0:yyyy-MM-dd HH:mm}' -f $sprx.LastWriteTime))"

# The client reads the console's build number and warns when it is older than the one it shipped
# against. A client that expects a build qwark has not got would warn about every console.
$protoText = Get-Content (Combine $qwark 'src' 'core' 'proto.h') -Raw
$clientText = Get-Content (Combine $repo 'src' 'RaCMAN.Protocol' 'QwarkClient.cs') -Raw

if ($protoText -notmatch '#define\s+QWARK_BUILD\s+(\d+)') { throw 'Could not read QWARK_BUILD from qwark.' }
$qwarkBuild = [int]$Matches[1]

if ($clientText -notmatch 'ExpectedQwarkBuild\s*=\s*(\d+)') { throw 'Could not read ExpectedQwarkBuild from the client.' }
$clientBuild = [int]$Matches[1]

if ($clientBuild -gt $qwarkBuild) {
    throw "The client expects qwark build $clientBuild and qwark is build $qwarkBuild. Bump QWARK_BUILD, or the client warns about every console."
}

if ($clientBuild -lt $qwarkBuild) {
    Write-Warning "qwark is build $qwarkBuild and the client expects $clientBuild. The client will not notice a console running the older module."
}
else {
    Write-Good "both sides agree on qwark build $qwarkBuild"
}

# ------------------------------------------------------------------------------------- the tests

if ($SkipTests) {
    Write-Step 'skipping the tests'
}
else {
    Invoke-Suite 'the client suite' { & dotnet test $repo --nologo --verbosity quiet }

    # qwark's suites need a POSIX shell and Python, which this machine has and a fresh one may not.
    # A missing tool is a warning: the console side is unchanged by a client-only release, and the
    # workflow never builds it. A suite that runs and fails is a release that stops.
    #
    # Git for Windows ships a shell and does not put it on the PATH, so a PATH lookup alone finds
    # nothing on a machine that has one. Git's comes first: qwark's build scripts are written for
    # it, and Cygwin's would need its own toolchain on the PATH to get through them.
    $sh = Get-Command sh -ErrorAction SilentlyContinue
    if (-not $sh) {
        foreach ($candidate in @(
            (Combine $env:ProgramFiles 'Git' 'bin' 'sh.exe'),
            (Combine ${env:ProgramFiles(x86)} 'Git' 'bin' 'sh.exe'),
            'C:\cygwin64\bin\sh.exe')) {

            if ($candidate -and (Test-Path $candidate)) {
                $sh = Get-Command $candidate
                break
            }
        }
    }

    $python = Get-Command python -ErrorAction SilentlyContinue

    # Both suites read paths relative to qwark's own root, so they run from there rather than from
    # wherever this script was called.
    Push-Location $qwark
    try {
        if ($sh) {
            Invoke-Suite "qwark's unit suite" { & $sh.Source 'test/run.sh' }
        }
        else {
            Write-Warning 'no sh found, so qwark test/run.sh did not run.'
        }

        if ($python) {
            Invoke-Suite "qwark's smoke test" { & $python.Source 'test/smoke.py' }
        }
        else {
            Write-Warning 'no python on the PATH, so qwark test/smoke.py did not run.'
        }
    }
    finally { Pop-Location }
}

# ------------------------------------------------------------------------------- say, ask, do

Write-Host ''
Write-Step "ready to release $tag"

foreach ($r in $toPush) {
    Write-Note "push $($r.Ahead) commit(s) to $($r.Name)'s $Remote/$Branch"
}

foreach ($r in $repos) {
    $at = (Invoke-Git $r.Path @('rev-parse', '--short', 'HEAD')).Trim()
    Write-Note "tag $($r.Name) $tag at $at and push it"
}

Write-Note 'the tag on this repository starts the release workflow'
Write-Host ''

if (-not $Yes -and -not $WhatIfPreference) {
    $answer = Read-Host "Release $tag? [y/N]"
    if ($answer -notmatch '^(y|yes)$') {
        Write-Host 'nothing was pushed' -ForegroundColor Yellow
        return
    }
}

foreach ($r in $toPush) {
    if ($PSCmdlet.ShouldProcess("$($r.Name) $Remote/$Branch", "push $($r.Ahead) commit(s)")) {
        Invoke-Git $r.Path @('push', $Remote, $Branch)
        Write-Good "pushed $($r.Name) to $Remote/$Branch"
    }
}

# qwark first. The client's workflow asks whether qwark has a tag of the same name and takes the
# module from it when it does, so tagging qwark second would leave the release built from main.
$pushed = @()
foreach ($r in $repos) {
    if (-not $PSCmdlet.ShouldProcess("$($r.Name) $tag", 'tag and push')) { continue }

    Invoke-Git $r.Path @('tag', '-a', $tag, '-m', "$($r.Name) $clean")

    try {
        Invoke-Git $r.Path @('push', $Remote, $tag)
    }
    catch {
        # Leaving a tag behind that the remote never saw would block the next attempt, and the
        # message would blame a tag this script had just made itself.
        Invoke-Git $r.Path @('tag', '-d', $tag) -AllowFail | Out-Null

        if ($pushed) {
            Write-Warning ("$tag is already on $($pushed -join ', '). Remove it there before trying again:`n" +
                           "    git -C <repo> push $Remote :refs/tags/$tag")
        }
        throw
    }

    $pushed += $r.Name
    Write-Good "tagged and pushed $($r.Name) $tag"
}

if ($WhatIfPreference) { return }

# The workflow lives here, so this repository's remote is where the run appears.
$url = (Invoke-Git $repo @('remote', 'get-url', $Remote)).Trim()
$url = $url -replace '\.git$', '' -replace '^git@github\.com:', 'https://github.com/'

Write-Host ''
Write-Host "released $tag" -ForegroundColor Green
Write-Note "$url/actions"
Write-Note "$url/releases/tag/$tag"
