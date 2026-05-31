param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "BrowserMusicController\BrowserMusicController.csproj"
$stagingDir = Join-Path $root "package_temp"
$outDll = Join-Path $root "BrowserMusicController\bin\$Configuration\BrowserMusicController.dll"
$outReadme = Join-Path $root "README.md"
$outPluginJson = Join-Path $root "BrowserMusicController\plugin.json"
$ymmePath = Join-Path $root "BrowserMusicController.ymme"
$tempYmmePath = Join-Path $root "BrowserMusicController.tmp.ymme"
$tempZipPath = Join-Path $root "BrowserMusicController.tmp.zip"

Write-Host "Building plugin..."
dotnet build $project -c $Configuration -p:SkipYmmPluginCopy=true

if (!(Test-Path $outDll)) {
    throw "DLL not found: $outDll"
}

New-Item -ItemType Directory -Force -Path $stagingDir | Out-Null
Copy-Item $outDll (Join-Path $stagingDir "BrowserMusicController.dll") -Force
Copy-Item $outPluginJson (Join-Path $stagingDir "plugin.json") -Force
Copy-Item $outReadme (Join-Path $stagingDir "README.md") -Force

if (Test-Path $tempYmmePath) {
    Remove-Item $tempYmmePath -Force
}
if (Test-Path $tempZipPath) {
    Remove-Item $tempZipPath -Force
}

Compress-Archive -Path (Join-Path $stagingDir "*") -DestinationPath $tempZipPath -Force
Move-Item -Path $tempZipPath -Destination $tempYmmePath -Force

try {
    Move-Item -Path $tempYmmePath -Destination $ymmePath -Force
    Write-Host "Updated: $ymmePath"
}
catch {
    $fallback = Join-Path $root ("BrowserMusicController." + (Get-Date -Format "yyyyMMdd-HHmmss") + ".ymme")
    Move-Item -Path $tempYmmePath -Destination $fallback -Force
    Write-Warning "Could not overwrite BrowserMusicController.ymme (possibly locked)."
    Write-Host "Created fallback package: $fallback"
}
