
$ErrorActionPreference = "Stop"

Set-Location (Join-Path $PSScriptRoot "kozos_rajzolo")

if (-not (Test-Path ".\wwwroot\lib\signalr\signalr.min.js")) {
    Write-Host "Hianyzik a SignalR kliens. Futtasd elobb: .\install-signalr.ps1" -ForegroundColor Yellow
    exit 2
}

Write-Host ".NET verzio:"
dotnet --version

Write-Host "NuGet csomagok visszaallitasa:"
dotnet restore

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Forditas:"
dotnet build --no-restore

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Inditas: http://localhost:5050"

dotnet run --no-build --urls "http://localhost:5050"

exit $LASTEXITCODE
