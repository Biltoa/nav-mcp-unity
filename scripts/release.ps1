<#
.SYNOPSIS
    Build every Windows artifact a release needs: three architectures, a drop and an installer each.

.DESCRIPTION
    x64 is what nearly everyone runs. arm64 is Windows on ARM, where an x64 build works through
    emulation but starts slower and burns battery. x86 is here for the same reason GitHub releases
    usually carry it — some people are still on 32-bit Windows, and a self-contained build is the
    only way they can run this at all.

    Each architecture gets a zipped folder drop and an installer, named so a download page reads
    unambiguously.

.EXAMPLE
    pwsh scripts/release.ps1
    pwsh scripts/release.ps1 -Runtimes win-x64
#>
[CmdletBinding()]
param(
    [string[]] $Runtimes = @('win-x64', 'win-arm64', 'win-x86'),
    [string]   $Version  = '1.0.1',
    [string]   $Output   = 'release'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$out = Join-Path $repo $Output
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $out | Out-Null

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

foreach ($rid in $Runtimes) {
    Write-Host "== $rid" -ForegroundColor Cyan

    # A hashtable splat, not an array: Windows PowerShell 5.1 turns an empty @() splat into a
    # $null positional argument, which the target script rejects with a binding error that names
    # neither the caller nor the parameter.
    #
    # Only the first pass runs the gates. They are architecture-independent, and each run costs a
    # full test pass plus a compile of the Unity package against a real Editor.
    $arguments = @{ Configuration = 'Release'; Runtime = $rid; Output = "dist-$rid" }
    if ($rid -ne $Runtimes[0]) { $arguments.SkipTests = $true }

    & (Join-Path $PSScriptRoot 'publish.ps1') @arguments
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $rid" }

    Compress-Archive -Path (Join-Path $repo "dist-$rid\*") `
                     -DestinationPath (Join-Path $out "NAV-MCP-$Version-$rid.zip") -Force

    if ($iscc) {
        # The installer script reads dist/, so point it at this architecture's drop.
        if (Test-Path (Join-Path $repo 'dist')) { Remove-Item (Join-Path $repo 'dist') -Recurse -Force }
        Copy-Item (Join-Path $repo "dist-$rid") (Join-Path $repo 'dist') -Recurse

        $arch = switch ($rid) { 'win-x64' { 'x64compatible' } 'win-arm64' { 'arm64' } default { 'x86' } }
        & $iscc /Qp "/DAppVersion=$Version" "/DArch=$arch" "/DSuffix=-$rid" (Join-Path $PSScriptRoot 'installer.iss')
        if ($LASTEXITCODE -ne 0) { throw "installer failed for $rid" }

        Move-Item (Join-Path $repo "dist-installer\NAV-MCP-Setup-$Version-$rid.exe") $out -Force
    }

    Remove-Item (Join-Path $repo "dist-$rid") -Recurse -Force
}

Write-Host ""
Write-Host "release artifacts:" -ForegroundColor Green
Get-ChildItem $out | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize
