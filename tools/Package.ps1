<#
.SYNOPSIS
  Builds ProjectMER in Release and writes the release archive dist\ProjectMER-Mobile-<version>.zip.

.DESCRIPTION
  The archive has one top folder, ProjectMER-Mobile-<version>\, containing:
    plugins\ProjectMER.dll, ProjectMER.pdb     copied by the server owner into LabAPI-Mobile\plugins\global
    README.md, README.zh-CN.md, NOTICE.md      from the repository root
    INSTALL.txt                                from tools\package\INSTALL.txt

  ProjectMER needs no other files: Newtonsoft.Json and YamlDotNet ship with the game, and LabApi.dll and
  0Harmony.dll come with LabAPI-Mobile. The script fails if ProjectMER.dll references any other assembly.

  The build needs a LabAPI-Mobile checkout next to this repository (or -LabApiMobileRoot) with its extracted
  reference server, because ProjectMER compiles against LabApi.csproj and the server's Managed folder.

.PARAMETER Version          Package version (default: <Version> of src\ProjectMER\ProjectMER.csproj).
.PARAMETER OutputDir        Where the zip and the staging folder go (default: <repo>\dist).
.PARAMETER ArtifactsPath    dotnet --artifacts-path for the build (default: <OutputDir>\build).
.PARAMETER LabApiMobileRoot LabAPI-Mobile checkout (default: the MSBuild default, ..\labapimobile).
.PARAMETER CarlManaged      Carl Mod server Managed folder (default: the MSBuild default, inside LabApiMobileRoot).
#>
[CmdletBinding()]
param(
    [string]$Version,
    [string]$OutputDir,
    [string]$ArtifactsPath,
    [string]$LabApiMobileRoot,
    [string]$CarlManaged
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repo 'src\ProjectMER\ProjectMER.csproj'
if (-not $OutputDir) { $OutputDir = Join-Path $repo 'dist' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
if (-not $ArtifactsPath) { $ArtifactsPath = Join-Path $OutputDir 'build' }
$ArtifactsPath = [IO.Path]::GetFullPath($ArtifactsPath)
$templates = Join-Path $PSScriptRoot 'package'

$msbuildProps = @()
if ($LabApiMobileRoot) { $msbuildProps += "-p:LabApiMobileRoot=$([IO.Path]::GetFullPath($LabApiMobileRoot))" }
if ($CarlManaged) { $msbuildProps += "-p:CarlManaged=$([IO.Path]::GetFullPath($CarlManaged))" }

if (-not $Version) {
    $Version = ([xml](Get-Content -LiteralPath $project -Raw)).Project.PropertyGroup.Version |
        Where-Object { $_ } | Select-Object -First 1
    if (-not $Version) { throw "No <Version> in $project; pass -Version." }
}

# The Managed folder the build uses, as MSBuild resolves it (Directory.Build.props and any overrides).
$CarlManaged = (& dotnet msbuild $project -nologo -getProperty:CarlManaged @msbuildProps).Trim()
if ($LASTEXITCODE -ne 0 -or -not $CarlManaged) { throw 'Could not evaluate CarlManaged from the project.' }
if (-not (Test-Path -LiteralPath (Join-Path $CarlManaged 'Assembly-CSharp.dll'))) {
    throw "Carl Mod server assemblies not found in $CarlManaged. Extract the server with labapimobile's tools/extract-server.py or pass -CarlManaged."
}

$commit = (& git -C $repo rev-parse --short HEAD 2>$null)
if (-not $commit) { $commit = 'unknown' }
if (& git -C $repo status --porcelain --untracked-files=no 2>$null) {
    Write-Warning 'The working tree has uncommitted changes; the package does not match a commit.'
    $commit = "$commit (with uncommitted changes)"
}

# Build.
Write-Host "Building ProjectMER (Release) into $ArtifactsPath"
& dotnet build $project -c Release --artifacts-path $ArtifactsPath -nologo -v quiet @msbuildProps
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
$merBin = Join-Path $ArtifactsPath 'bin\ProjectMER\release'
$merDll = Join-Path $merBin 'ProjectMER.dll'
if (-not (Test-Path -LiteralPath $merDll)) { throw "Build output missing: $merDll" }

# ProjectMER must not need anything the game, LabApi or Harmony do not provide.
$provided = @{}
Get-ChildItem -LiteralPath $CarlManaged -Filter *.dll | ForEach-Object { $provided[$_.BaseName] = $true }
foreach ($n in 'LabApi', '0Harmony') { $provided[$n] = $true }
$merRefs = [Reflection.AssemblyName[]]([Reflection.Assembly]::Load([IO.File]::ReadAllBytes($merDll)).GetReferencedAssemblies())
$missing = @($merRefs | Where-Object { -not $provided.ContainsKey($_.Name) } | ForEach-Object Name)
if ($missing.Count -gt 0) { throw "ProjectMER.dll references assemblies that neither the game, LabApi nor Harmony provide: $($missing -join ', ')" }

# Stage.
$name = "ProjectMER-Mobile-$Version"
$stage = Join-Path $OutputDir $name
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage, (Join-Path $stage 'plugins') | Out-Null

Copy-Item -LiteralPath $merDll -Destination (Join-Path $stage 'plugins')
$merPdb = Join-Path $merBin 'ProjectMER.pdb'
if (Test-Path -LiteralPath $merPdb) { Copy-Item -LiteralPath $merPdb -Destination (Join-Path $stage 'plugins') }
foreach ($doc in 'README.md', 'README.zh-CN.md', 'NOTICE.md') {
    Copy-Item -LiteralPath (Join-Path $repo $doc) -Destination $stage
}

# INSTALL.txt with CRLF line endings and placeholders filled in.
$text = [IO.File]::ReadAllText((Join-Path $templates 'INSTALL.txt'))
$text = $text.Replace('{{VERSION}}', $Version).Replace('{{COMMIT}}', $commit)
$text = ($text -replace "`r`n", "`n") -replace "`n", "`r`n"
[IO.File]::WriteAllText((Join-Path $stage 'INSTALL.txt'), $text, (New-Object Text.UTF8Encoding($false)))

# Zip with forward-slash entry names under one top folder.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zipPath = Join-Path $OutputDir "$name.zip"
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entry = "$name/" + $_.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $_.FullName, $entry, [IO.Compression.CompressionLevel]::Optimal)
    }
} finally { $zip.Dispose() }

Write-Host ''
Write-Host "Package: $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1KB)) KB)"
Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($stage.Length + 1)
}
