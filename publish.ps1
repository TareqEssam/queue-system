# نشر نسخة Windows portable (شغّله على جهاز Windows مع .NET 8 SDK)
# Usage: powershell -ExecutionPolicy Bypass -File publish.ps1

$ErrorActionPreference = "Stop"
$Out = Join-Path $PSScriptRoot "publish-win"

Write-Host "==> Publishing self-contained win-x64..." -ForegroundColor Cyan
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -o $Out

Write-Host "==> Output: $Out" -ForegroundColor Green
Write-Host "Copy this folder to the target PC. No admin rights needed."
Write-Host "Optional: place cloudflared.exe next to QueueSystem.exe for public tunnel."
