<#
.SYNOPSIS
    Build a Windows release drop of NAV MCP into dist/.

.DESCRIPTION
    Produces everything an install needs and nothing it does not:

        dist/
          NAV MCP.exe               the app people double-click
          umcpd.exe                 the server it starts
          umcp-stdio.exe            the MCP shim clients spawn
          com.umcp.agent/           the Unity package, referenced from a project manifest
          INSTALL.md                the install guide, copied for the drop

    Self-contained by default. The audience for this drop is someone who has never installed a
    .NET runtime and should not have to; the cost is about 70 MB of files nobody has to think
    about. Pass -FrameworkDependent for the small drop that needs the .NET 8 runtime present.

.EXAMPLE
    pwsh scripts/publish.ps1
    pwsh scripts/publish.ps1 -FrameworkDependent
    pwsh scripts/publish.ps1 -Runtime win-arm64
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [ValidateSet('win-x64', 'win-arm64')]
    [string] $Runtime = 'win-x64',
    [switch] $FrameworkDependent,
    [switch] $SkipTests,
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

# The Unity half is never compiled by the tests — Unity does that, on somebody else's machine,
# minutes later. A missing using or a renamed enum member would ship green.
Write-Host "== the agent compiles against a real Editor" -ForegroundColor Cyan
& (Join-Path $PSScriptRoot 'check-agent-compile.ps1')
if ($LASTEXITCODE -ne 0) { throw "the Unity package does not compile" }

Write-Host "== the rule the compiler cannot enforce" -ForegroundColor Cyan
dotnet run --project src/Umcp.MainThreadCheck -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "main-thread check failed" }

if (-not $SkipTests) {
    Write-Host "== tests" -ForegroundColor Cyan
    dotnet test UnityMcpTool.sln -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { throw "tests failed" }
}

$selfContained = -not $FrameworkDependent
$publishArgs = @('-c', $Configuration, '-r', $Runtime, '--self-contained', $(if ($selfContained) { 'true' } else { 'false' }))

# The GUI is published last so its own copies of the shared framework files win any collision —
# they are identical, but a half-overwritten runtime is not a thing to leave to file order.
foreach ($project in @('src/Umcp.Daemon', 'src/Umcp.Stdio', 'src/Umcp.Gui')) {
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

# The app is what a person launches, so its absence is a broken drop, not a warning.
foreach ($required in @('NAV MCP.exe', 'umcpd.exe', 'umcp-stdio.exe')) {
    if (-not (Test-Path (Join-Path $dist $required))) { throw "the drop is missing $required" }
}

Write-Host ""
Write-Host "dist/ is ready: $version ($Runtime, $(if ($selfContained) { 'self-contained' } else { 'needs the .NET 8 runtime' }))" -ForegroundColor Green
Write-Host "Start it by double-clicking 'NAV MCP.exe'." -ForegroundColor Green
Write-Host ""
Write-Host "Unsigned, so the first launch shows SmartScreen: More info -> Run anyway." -ForegroundColor DarkYellow
Get-ChildItem $dist | Select-Object Name, Length | Format-Table -AutoSize
