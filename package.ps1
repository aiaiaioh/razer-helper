# Builds both release files into dist\:
#   RazerHelper-Setup-<version>.exe   the installer (needs Inno Setup 6 or 7)
#   RazerHelper-<version>-portable.zip  the portable version (just unzip and run)
#
# Run from the repo root:   .\package.ps1 -Version 1.1.0
# Needs the .NET 10 SDK. The installer also needs Inno Setup 6 or 7.
# Add -SelfContained to bundle .NET into the portable zip (much bigger, but needs no runtime).
param(
    [string]$Version = "1.1.0",
    [switch]$SelfContained
)

$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"
$project = Join-Path $root "src\RazerHelper\RazerHelper.csproj"

New-Item -ItemType Directory -Force $dist | Out-Null

# 1. Installer: the existing script publishes the app, then runs Inno Setup.
& (Join-Path $root "installer\build.ps1") -Version $Version
if ($LASTEXITCODE -ne 0) { throw "Installer build failed" }

# 2. Portable: publish again into its own folder so the installer's build is untouched.
$stage = Join-Path $root "publish-portable"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

$sc = if ($SelfContained) { "true" } else { "false" }
dotnet publish $project -c Release -r win-x64 --self-contained $sc `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$Version `
    -o $stage
if ($LASTEXITCODE -ne 0) { throw "dotnet publish (portable) failed" }

# Ship the licence and readme next to the exe.
foreach ($file in "README.md", "LICENSE", "CHANGELOG.md") {
    $path = Join-Path $root $file
    if (Test-Path $path) { Copy-Item $path $stage }
}

$suffix = if ($SelfContained) { "portable-selfcontained" } else { "portable" }
$zip = Join-Path $dist "RazerHelper-$Version-$suffix.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip
Remove-Item $stage -Recurse -Force

Write-Host ""
Write-Host "Done. Upload these to a GitHub release:" -ForegroundColor Green
Get-ChildItem $dist | Where-Object { $_.Name -like "*$Version*" } |
    ForEach-Object { Write-Host "  $($_.FullName)  ($([math]::Round($_.Length / 1MB, 1)) MB)" }
