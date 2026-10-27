param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "Building Love Nest Launcher ($Configuration)..." -ForegroundColor Magenta
dotnet build "$root\LoveNestLauncher.sln" -c $Configuration -v:minimal

if ($LASTEXITCODE -ne 0) {
    throw "Build failed."
}

Write-Host "Build succeeded." -ForegroundColor Green
