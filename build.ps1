<#
.SYNOPSIS
    Builds MemReduct, or publishes standalone executables.

.DESCRIPTION
    Without -Publish this does a normal framework-dependent build of the solution,
    which is what day-to-day development wants: it is fast and needs no runtime packs.

    With -Publish it produces single-file executables for both front ends under
    artifacts/publish/. Those are self-contained by default, so they run on a machine
    with no .NET installed at all; pass -SelfContained:$false for the much smaller
    framework-dependent variant. It also copies each executable, under a friendlier
    name, into dist/ - one file per front end and nothing else - so the result can be
    double-clicked or copied elsewhere on its own.

.PARAMETER Configuration
    Debug or Release. Defaults to Release.

.PARAMETER Publish
    Produce single-file executables instead of a plain build.

.PARAMETER Runtime
    Target runtime identifier for -Publish. Defaults to win-x64.

.PARAMETER SelfContained
    Bundle the .NET runtime into the published executables. Defaults to true.
    Only meaningful together with -Publish.

.PARAMETER SignPfx
    Optional Authenticode certificate (PFX) used to sign both published executables. The
    password, when needed, comes from MEMREDUCT_SIGN_PASSWORD. Without this parameter the
    publish is unsigned.

.PARAMETER TimestampUrl
    RFC 3161 timestamp server used while signing. Defaults to DigiCert.

.EXAMPLE
    .\build.ps1

.EXAMPLE
    .\build.ps1 -Publish

.EXAMPLE
    .\build.ps1 -Publish -Runtime win-arm64 -SelfContained:$false

.EXAMPLE
    $env:MEMREDUCT_SIGN_PASSWORD = '...'
    .\build.ps1 -Publish -SignPfx .\cert\MemReduct.pfx
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [switch] $Publish,

    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',

    [bool] $SelfContained = $true,

    [string] $SignPfx = '',

    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is not populated during parameter binding, so resolve it here.
$root = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$solution = Join-Path $root 'MemReduct.sln'

if (-not (Test-Path $solution)) {
    throw "Solution not found at $solution"
}

# The GUI project embeds app.ico. Regenerate it if a fresh clone has not built yet.
$icon = Join-Path $root 'src\MemReduct.Gui\app.ico'
if (-not (Test-Path $icon)) {
    Write-Host 'app.ico missing - regenerating...' -ForegroundColor DarkGray
    & (Join-Path $root 'tools\make-icon.ps1')
}

if (-not $Publish) {
    Write-Host "Building solution ($Configuration)..." -ForegroundColor Cyan
    & dotnet build $solution -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE." }

    Write-Host ''
    Write-Host 'Output:' -ForegroundColor Cyan
    foreach ($app in 'MemReduct.Cli', 'MemReduct.Gui') {
        $exe = Join-Path $root "src\$app\bin\$Configuration\net8.0-windows"
        Get-ChildItem -Path $exe -Filter '*.exe' -ErrorAction SilentlyContinue |
            ForEach-Object { Write-Host ("  {0}" -f $_.FullName) }
    }

    return
}

# ------------------------------------------------------------------ publish

$outputRoot = Join-Path $root "artifacts\publish\$Runtime"
$kind = if ($SelfContained) { 'self-contained' } else { 'framework-dependent' }

Write-Host "Publishing $Runtime ($Configuration, $kind)..." -ForegroundColor Cyan

# Note: trimming is deliberately never enabled. WinForms is not trim-safe, and a
# trimmed build fails at runtime rather than at publish time.
$shared = @(
    '-c', $Configuration,
    '-r', $Runtime,
    "--self-contained:$($SelfContained.ToString().ToLowerInvariant())",
    '-p:PublishSingleFile=true',
    # PublishSingleFile on its own bundles only managed assemblies. The Windows Desktop
    # runtime pack's native libraries (wpfgfx_cor3, PresentationNative_cor3, PenImc_cor3,
    # D3DCompiler_47_cor3, vcruntime140_cor3) would be left sitting next to the exe, so the
    # "single file" would still need its folder. This embeds them; they unpack to a temp
    # directory on first run.
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=none',
    '--nologo'
)

if ($SelfContained) {
    # Roughly halves a self-contained WinForms executable, at the cost of startup time.
    $shared += '-p:EnableCompressionInSingleFile=true'
}

$targets = @(
    @{ Name = 'memreduct'; Dist = 'MemReduct-CLI'; Project = 'src\MemReduct.Cli\MemReduct.Cli.csproj' },
    @{ Name = 'memreduct-gui'; Dist = 'MemReduct'; Project = 'src\MemReduct.Gui\MemReduct.Gui.csproj' }
)

# dist\ is the double-click surface: exactly one executable per front end, nothing else,
# so it is obvious which file to run and the file can be copied out on its own.
$dist = Join-Path $root 'dist'
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

foreach ($target in $targets) {
    $project = Join-Path $root $target.Project
    $out = Join-Path $outputRoot $target.Name

    Write-Host "  -> $($target.Name)" -ForegroundColor DarkGray
    & dotnet publish $project @shared -o $out
    if ($LASTEXITCODE -ne 0) { throw "Publish of $($target.Name) failed with exit code $LASTEXITCODE." }

    $exe = Join-Path $out "$($target.Name).exe"
    if (-not (Test-Path $exe)) { throw "Expected $exe but it was not produced." }

    Copy-Item $exe (Join-Path $dist "$($target.Dist).exe")
}

# ---------------------------------------------------------------- integrity

# Windows runs a process at the lower of the user's integrity level and the executable's
# mandatory label. A workspace copied out of a sandbox can carry a Low label on its folders;
# executables published into it then inherit Low, and a Low process cannot write to %TEMP% or
# %APPDATA%. The single-file host cannot extract its native libraries and WinForms cannot create
# its activation context, so the app exits before its first window. Explorer also treats the file
# as untrusted and shows an unknown-publisher warning.
#
# Changing a mandatory label needs SeRelabelPrivilege, so the work is delegated to a helper that
# re-launches itself elevated. The stamp keeps subsequent publishes from prompting for a label
# that the repository already has.
$stamp = Join-Path $root 'artifacts\.integrity-medium'
$fingerprint = "$((Get-Acl -LiteralPath $root).Owner)|$((Get-Item -LiteralPath $root).CreationTimeUtc.Ticks)"
$needsRelabel = -not (Test-Path -LiteralPath $stamp) -or
    (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $fingerprint

if ($needsRelabel) {
    & (Join-Path $root 'tools\fix-integrity.ps1') -Root $root -Quiet
}

# ------------------------------------------------------------------- signing

if ($SignPfx) {
    if (-not (Test-Path -LiteralPath $SignPfx)) {
        throw "Signing certificate not found: $SignPfx"
    }

    $signtool = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty Source

    if (-not $signtool) {
        $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
        $signtool = Get-ChildItem -Path $kits -Filter 'signtool.exe' -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1 -ExpandProperty FullName
    }

    if (-not $signtool) {
        throw 'signtool.exe was not found. Install the Windows SDK or put it on PATH.'
    }

    # The password stays in the environment rather than on the command line.
    $password = $env:MEMREDUCT_SIGN_PASSWORD

    Get-ChildItem -Path $dist -Filter '*.exe' -File | ForEach-Object {
        $arguments = @('sign', '/fd', 'SHA256', '/f', $SignPfx)
        if ($password) { $arguments += @('/p', $password) }
        if ($TimestampUrl) { $arguments += @('/tr', $TimestampUrl, '/td', 'SHA256') }
        $arguments += $_.FullName

        & $signtool @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Signing $($_.Name) failed with exit code $LASTEXITCODE."
        }

        $signature = Get-AuthenticodeSignature -LiteralPath $_.FullName
        if ($signature.Status -ne 'Valid') {
            throw "Signature verification for $($_.Name) returned $($signature.Status)."
        }

        Write-Host ("  signed {0} ({1})" -f $_.Name, $signature.SignerCertificate.Subject) -ForegroundColor DarkGray
    }
} else {
    Write-Host ''
    Write-Host '  Signing skipped: no -SignPfx supplied. The published files are unsigned;' -ForegroundColor Yellow
    Write-Host '  SmartScreen reputation on other machines requires an Authenticode certificate.' -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Double-click these:' -ForegroundColor Cyan
Get-ChildItem -Path $dist -Filter '*.exe' |
    ForEach-Object { Write-Host ("  {0}  ({1:N1} MB)" -f $_.FullName, ($_.Length / 1MB)) }
