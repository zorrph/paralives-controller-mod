<#
.SYNOPSIS
    Builds ControllerMod in Release, stages the user-facing zip layout, and optionally
    publishes a GitHub release.

.EXAMPLE
    .\release.ps1                 # build + zip into .\dist
    .\release.ps1 -Publish        # build + zip + `gh release create v<version>`
    .\release.ps1 -GamePath "D:\Games\Paralives"
#>
param(
    [switch]$Publish,
    [string]$GamePath = "F:\Steam\steamapps\common\Paralives"
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$projectDir = $repoRoot
$pluginSource = Join-Path $projectDir "Plugin.cs"

# Version is authored in one place: the PluginVersion constant in Plugin.cs.
$versionMatch = Select-String -Path $pluginSource -Pattern 'PluginVersion\s*=\s*"([0-9]+\.[0-9]+\.[0-9]+)"'
if (-not $versionMatch) { throw "Could not find PluginVersion in Plugin.cs" }
$version = $versionMatch.Matches[0].Groups[1].Value
Write-Host "Building Paralives Controller Mod v$version" -ForegroundColor Cyan

dotnet build (Join-Path $projectDir "ControllerMod.csproj") -c Release -p:GamePath="$GamePath" -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed" }

# Stage the zip so it extracts directly into the Paralives folder.
$dist = Join-Path $repoRoot "dist"
$stage = Join-Path $dist "stage\BepInEx\plugins"
if (Test-Path (Join-Path $dist "stage")) { Remove-Item (Join-Path $dist "stage") -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item (Join-Path $projectDir "bin\Release\netstandard2.0\ControllerMod.dll") $stage

$zipPath = Join-Path $dist "Paralives-ControllerMod-v$version.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $dist "stage\BepInEx") -DestinationPath $zipPath
Remove-Item (Join-Path $dist "stage") -Recurse -Force
Write-Host "Staged: $zipPath ($([math]::Round((Get-Item $zipPath).Length / 1KB)) KB)" -ForegroundColor Green

if ($Publish) {
    gh release create "v$version" $zipPath --title "v$version" --generate-notes
    if ($LASTEXITCODE -ne 0) { throw "gh release create failed (is the repo pushed to GitHub and gh authenticated?)" }
    Write-Host "Published release v$version" -ForegroundColor Green
}
