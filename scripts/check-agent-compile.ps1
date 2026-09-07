<#
.SYNOPSIS
    Compile the Unity package against a real Editor's assemblies, without opening Unity.

.DESCRIPTION
    The daemon's tests never compile the Unity half — Unity does that, on the developer's machine,
    minutes later. So a missing `using`, a renamed enum member or a method that does not exist on
    this Unity version ships green and breaks in somebody's Editor.

    This finds an installed Editor, generates a throwaway csproj referencing its UnityEngine and
    UnityEditor *modules*, and compiles every .cs in the package. It is not Unity's compiler and it
    does not run anything; it answers one question — does this code exist and type-check against
    this Editor version.

    Two details that matter:
      * The monolithic UnityEditor.dll and UnityEngine.dll are facades that type-forward into the
        modules. Referencing both gives every type twice and 40 CS0433 errors that mean nothing.
      * Newtonsoft.Json comes from a project's package cache, because the agent's asmdef takes it
        as a precompiled reference.

.EXAMPLE
    pwsh scripts/check-agent-compile.ps1
    pwsh scripts/check-agent-compile.ps1 -EditorPath "E:\Unity Editor\6000.3.10f1"
#>
[CmdletBinding()]
param(
    [string] $EditorPath,
    [string] $NewtonsoftDll,
    [string] $WorkDir = (Join-Path ([System.IO.Path]::GetTempPath()) "umcp-agent-compile")
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# ---------------------------------------------------------------- find an Editor

if (-not $EditorPath) {
    $roots = @()
    $secondary = Join-Path $env:APPDATA 'UnityHub\secondaryInstallPath.json'
    if (Test-Path $secondary) {
        $path = (Get-Content $secondary -Raw).Trim().Trim('"')
        if ($path) { $roots += $path }
    }
    $roots += 'C:\Program Files\Unity\Hub\Editor'

    $found = foreach ($root in $roots) {
        if (Test-Path $root) {
            Get-ChildItem $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -match '^\d+\.\d+\.\d+[abfp]\d+$' -and (Test-Path (Join-Path $_.FullName 'Editor\Data\Managed')) }
        }
    }
    $EditorPath = ($found | Sort-Object Name -Descending | Select-Object -First 1).FullName
}

if (-not $EditorPath -or -not (Test-Path $EditorPath)) {
    Write-Host "No Unity Editor found. Pass -EditorPath, or skip this check." -ForegroundColor Yellow
    exit 0            # absence of an Editor is not a failing build
}

$managed = Join-Path $EditorPath 'Editor\Data\Managed'
Write-Host "== compiling the agent against $(Split-Path $EditorPath -Leaf)" -ForegroundColor Cyan

# ---------------------------------------------------------------- find Newtonsoft

if (-not $NewtonsoftDll) {
    $NewtonsoftDll = Get-ChildItem (Join-Path $EditorPath 'Editor\Data\Resources\PackageManager') `
        -Filter 'Newtonsoft.Json.dll' -Recurse -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $NewtonsoftDll) {
    # A project's package cache is the reliable copy; the agent's asmdef takes it as a
    # precompiled reference, so Unity resolves it the same way.
    $NewtonsoftDll = Get-ChildItem 'E:\', 'D:\', 'C:\Users' -Filter 'Newtonsoft.Json.dll' -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'PackageCache\\com\.unity\.nuget\.newtonsoft-json' -and $_.FullName -notmatch 'AOT' } |
        Select-Object -First 1 -ExpandProperty FullName
}
if (-not $NewtonsoftDll) {
    Write-Host "Newtonsoft.Json.dll not found; skipping (the agent references it via its asmdef)." -ForegroundColor Yellow
    exit 0
}

# ---------------------------------------------------------------- generate and build

$references = @()
foreach ($dir in @((Join-Path $managed 'UnityEngine'), $managed)) {
    if (-not (Test-Path $dir)) { continue }
    foreach ($dll in Get-ChildItem $dir -Filter '*.dll' -File) {
        $name = [System.IO.Path]::GetFileNameWithoutExtension($dll.Name)
        if ($name -in @('UnityEditor', 'UnityEngine')) { continue }          # facades, see above
        if ($name -notmatch '^Unity(Engine|Editor)') { continue }
        if ($references.Name -contains $name) { continue }
        $references += [pscustomobject]@{ Name = $name; Path = $dll.FullName }
    }
}
$references += [pscustomobject]@{ Name = 'Newtonsoft.Json'; Path = $NewtonsoftDll }

New-Item -ItemType Directory -Force -Path $WorkDir | Out-Null
$refXml = ($references | ForEach-Object {
    "    <Reference Include=`"$($_.Name)`"><HintPath>$($_.Path)</HintPath></Reference>"
}) -join "`n"

@"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.1</TargetFramework>
    <LangVersion>9</LangVersion>
    <Nullable>disable</Nullable>
    <ImplicitUsings>disable</ImplicitUsings>
    <EnableDefaultCompileItems>false</EnableDefaultCompileItems>
    <DefineConstants>UNITY_EDITOR;UNITY_2021_1_OR_NEWER;UNITY_6000_0_OR_NEWER;UNITY_STANDALONE_WIN;UNITY_64</DefineConstants>
    <NoWarn>CS0618;CS0612;CS0649;CS0169;CS0414</NoWarn>
    <AssemblyName>AgentCompileCheck</AssemblyName>
  </PropertyGroup>
  <ItemGroup>
    <Compile Include="$repo\unity\com.umcp.agent\Editor\**\*.cs" />
  </ItemGroup>
  <ItemGroup>
$refXml
  </ItemGroup>
</Project>
"@ | Set-Content (Join-Path $WorkDir 'AgentCompileCheck.csproj') -Encoding utf8

Push-Location $WorkDir
try {
    $output = & dotnet build -v q --nologo 2>&1
    $errors = $output | Select-String 'error CS'
    if ($errors) {
        $errors | Select-Object -Unique | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        throw "the Unity package does not compile against $(Split-Path $EditorPath -Leaf)"
    }
}
finally { Pop-Location }

Write-Host "the agent compiles against $(Split-Path $EditorPath -Leaf)" -ForegroundColor Green
