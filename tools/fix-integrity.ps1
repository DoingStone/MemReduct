<#
.SYNOPSIS
    Resets this workspace and its published executables to the Medium integrity level.

.DESCRIPTION
    Windows computes a process's integrity level as the minimum of the user's level and the
    executable's mandatory label. A workspace copied out of a sandbox can carry a Low label on
    its folders; every executable published into it then inherits Low, and a Low process cannot
    write to %TEMP% or %APPDATA% (both are Medium, and there is no low mandatory label on them).
    That breaks the single-file host's extraction and WinForms' activation context, so the app
    exits before it can show anything. Explorer also treats the file as untrusted and shows the
    "unknown publisher" security warning.

    Setting a mandatory label needs SeRelabelPrivilege, so this script re-launches itself
    elevated. It is idempotent: the repository root gets an inheritable Medium label, and the
    current dist\ contents are relabelled explicitly.

.PARAMETER Root
    Repository root. Defaults to the parent of this script's directory.

.PARAMETER Quiet
    Suppresses the one-line status output.
#>
[CmdletBinding()]
param(
    [string] $Root,
    [switch] $Quiet
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$scriptPath = $PSCommandPath
$rootPath = if ($Root) { (Resolve-Path -LiteralPath $Root).Path } else { Split-Path -Parent $PSScriptRoot }
$rootPath = $rootPath.TrimEnd('\')

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isElevated = ([Security.Principal.WindowsPrincipal] $identity).
    IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

if (-not $isElevated) {
    if (-not $Quiet) {
        Write-Host 'Integrity label: elevating to set the workspace to Medium...' -ForegroundColor DarkGray
    }

    $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Root `"$rootPath`" -Quiet"
    $process = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $arguments -Wait -PassThru

    if ($process.ExitCode -ne 0) {
        throw "Could not reset the workspace integrity label (elevated helper exited with $($process.ExitCode))."
    }

    return
}

$icacls = Join-Path $env:SystemRoot 'System32\icacls.exe'
$dist = Join-Path $rootPath 'dist'

# The root gets an inheritable label so every folder and file created by later builds inherits
# Medium.
& $icacls $rootPath /setintegritylevel '(OI)(CI)Medium' | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw "icacls could not relabel the repository root (exit $LASTEXITCODE)."
}

# Existing children inherited the old Low label. Relabel them best-effort: a file held open by
# a running build cannot be relabelled, but the next build recreates it under the Medium root.
& $icacls $rootPath /setintegritylevel Medium /T /C | Out-Null

if (Test-Path $dist) {
    & $icacls $dist /setintegritylevel '(OI)(CI)Medium' /T /C | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "icacls could not relabel the published files (exit $LASTEXITCODE)."
    }

    Get-ChildItem -Path $dist -Filter '*.exe' -File |
        ForEach-Object {
            & $icacls $_.FullName /setintegritylevel Medium | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "icacls could not relabel $($_.FullName) (exit $LASTEXITCODE)."
            }
        }
}

# build.ps1 skips the elevation when the workspace fingerprint has not changed. The owner and
# the folder's creation time change when the repository is copied in from another machine.
$stampDirectory = Join-Path $rootPath 'artifacts'
New-Item -ItemType Directory -Path $stampDirectory -Force | Out-Null
$stamp = Join-Path $stampDirectory '.integrity-medium'
$owner = (Get-Acl -LiteralPath $rootPath).Owner
$created = (Get-Item -LiteralPath $rootPath).CreationTimeUtc.Ticks
"$owner|$created" | Set-Content -LiteralPath $stamp -Encoding ASCII

if (-not $Quiet) {
    Write-Host "Integrity label: $rootPath is Medium." -ForegroundColor DarkGray
}
