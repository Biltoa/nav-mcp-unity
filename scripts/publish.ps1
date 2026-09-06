<#
.SYNOPSIS
    Build a release drop of the Unity MCP Tool into dist/.

.DESCRIPTION
    Produces everything an install needs and nothing it does not:

        dist/
          umcpd.exe                 the daemon
          umcp-stdio.exe            the MCP stdio shim
          com.umcp.agent/           the Unity package, to reference from a project manifest
          INSTALL.md                the install guide, copied for the drop

    Framework-dependent by default: it needs the .NET 8 runtime, which is a 60 MB shared install
    rather than a 70 MB copy inside every build. Pass -SelfContained for a machine that has no
    runtime at all.

.EXAMPLE
    pwsh scripts/publish.ps1
    pwsh scripts/publish.ps1 -SelfContained
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [switch] $SelfContained,
    [string] $Output = 'dist'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo

$dist = Join-Path $repo $Output
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Path $dist | Out-Null

Write-Host "== generating tools from the [UnityTool] sources" -ForegroundColor Cyan
dotnet run --project src/Umcp.ToolGen -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "toolgen failed" }

# Generated output is committed; a release built from a tree where it differs is a release whose
# catalog does not match its sources.
$drift = git status --porcelain -- unity/com.umcp.agent/Editor/Generated src/Umcp.Daemon/Generated docs/TOOLS.md
if ($drift) { throw "generated files are out of date; run toolgen and commit before publishing:`n$drift" }

Write-Host "== the rule the compiler cannot enforce" -ForegroundColor Cyan
dotnet run --project src/Umcp.MainThreadCheck -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "main-thread check failed" }

Write-Host "== tests" -ForegroundColor Cyan
dotnet test UnityMcpTool.sln -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) { throw "tests failed" }

$publishArgs = @('-c', $Configuration, '-r', $Runtime, '--self-contained', $(if ($SelfContained) { 'true' } else { 'false' }))

foreach ($project in @('src/Umcp.Daemon', 'src/Umcp.Stdio')) {
    $name = Split-Path $project -Leaf
    Write-Host "== publishing $name" -ForegroundColor Cyan
    $target = Join-Path $dist ".stage-$name"
    dotnet publish $project @publishArgs -o $target --nologo
    if ($LASTEXITCODE -ne 0) { throw "publish failed for $project" }

    Copy-Item (Join-Path $target '*') $dist -Recurse -Force
    Remove-Item $target -Recurse -Force
}

Write-Host "== copying the Unity package" -ForegroundColor Cyan
Copy-Item (Join-Path $repo 'unity/com.umcp.agent') (Join-Path $dist 'com.umcp.agent') -Recurse -Force
Copy-Item (Join-Path $repo 'docs/INSTALL.md') $dist -Force
Copy-Item (Join-Path $repo 'docs/TOOLS.md') $dist -Force

# Strip the build noise a Unity package drop does not need.
Get-ChildItem (Join-Path $dist 'com.umcp.agent') -Recurse -Include 'Library', 'Temp', 'obj', 'bin' -Directory |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

$version = (& (Join-Path $dist 'umcpd.exe') --version)
Write-Host ""
Write-Host "dist/ is ready: $version" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
