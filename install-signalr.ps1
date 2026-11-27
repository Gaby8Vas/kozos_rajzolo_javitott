# Egyetlen alkalommal, internetkapcsolattal kell futtatni.
# A hivatalos SignalR böngészős kliens a projekt wwwroot mappájába kerül.
$ErrorActionPreference = 'Stop'
$destination = Join-Path $PSScriptRoot 'kozos_rajzolo\wwwroot\lib\signalr\signalr.min.js'
New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
$urls = @(
    'https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/8.0.7/signalr.min.js',
    'https://cdn.jsdelivr.net/npm/@microsoft/signalr@8.0.7/dist/browser/signalr.min.js'
)
foreach ($url in $urls) {
    try {
        Invoke-WebRequest -Uri $url -OutFile $destination -UseBasicParsing -TimeoutSec 30
        if ((Get-Item $destination).Length -lt 10000) {
            throw 'Gyanúsan rövid, nem teljes fájl.'
        }
        Write-Host "SignalR kliens elmentve: $destination" -ForegroundColor Green
        exit 0
    } catch {
        Write-Warning "Sikertelen letöltés: $url - $($_.Exception.Message)"
    }
}
Write-Error 'Nem sikerült beszerezni a SignalR klienst. Internetkapcsolattal futtasd újra, vagy másold az eredeti CDN-fájlt a megadott mappába.'
exit 1
