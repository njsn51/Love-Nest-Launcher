param(
    [string]$OutputDirectory = "dist"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$output = Join-Path $root $OutputDirectory
New-Item -ItemType Directory -Force -Path $output | Out-Null

Write-Host "Publishing single-file Love Nest Launcher..." -ForegroundColor Magenta
dotnet publish "$root\src\LoveNestLauncher\LoveNestLauncher.csproj" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -o $output

if ($LASTEXITCODE -ne 0) {
    throw "Publish failed."
}

$builtExe = Join-Path $output "LoveNestLauncher.exe"
$finalExe = Join-Path $output "Love Nest Launcher.exe"
if (Test-Path $finalExe) {
    Remove-Item -LiteralPath $finalExe -Force
}

Move-Item -LiteralPath $builtExe -Destination $finalExe
Write-Host "Published: $finalExe" -ForegroundColor Green
