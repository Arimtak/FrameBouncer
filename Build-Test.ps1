# Build-Test.ps1
# Baut den NEUESTEN FrameBouncer-Stand (App + ElevationHelper + Updater) als
# self-contained win-x64 Release und legt ihn in einen NEUEN, nummerierten
# Testordner ab. Fruehere Staende bleiben unveraendert erhalten:
#
#   FrameBouncer-Test-1   (aeltester Stand)
#   FrameBouncer-Test-2   (naechster Stand)
#   FrameBouncer-Test-3   (neuester Stand)   <-- wird bei jedem Lauf neu erzeugt
#
# Aufruf:  powershell -NoProfile -ExecutionPolicy Bypass -File Build-Test.ps1
# Oder:    Doppelklick auf "FrameBouncer Test-Build bauen.cmd" (Desktop)

param(
    # Basisname fuer die nummerierten Testordner (Standard: Desktop\FrameBouncer-Test)
    [string]$TargetBase = (Join-Path ([Environment]::GetFolderPath('Desktop')) 'FrameBouncer-Test')
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$parent   = Split-Path -Parent $TargetBase
$prefix   = Split-Path -Leaf $TargetBase
$staging  = Join-Path $repoRoot '.test-publish'

function Invoke-Publish {
    param([string]$Project, [string]$OutDir)
    Write-Host "Publishe $Project ..." -ForegroundColor Cyan
    dotnet publish (Join-Path $repoRoot $Project) -c Release -r win-x64 --self-contained true -o $OutDir --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Publish fehlgeschlagen: $Project" }
}

# 1) Laufende Instanz? Dann abbrechen (FrameBouncer.exe ist dann gesperrt)
$running = Get-Process -Name FrameBouncer -ErrorAction SilentlyContinue
if ($running) {
    Write-Host "FrameBouncer laeuft noch (PID $($running.Id -join ', '))." -ForegroundColor Yellow
    Write-Host 'Bitte erst beenden, dann das Skript erneut ausfuehren.' -ForegroundColor Yellow
    exit 1
}

# 2) Naechste freie Nummer bestimmen (bestehende Staende bleiben erhalten)
$existing = Get-ChildItem $parent -Directory -Filter "$prefix-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match "^$([regex]::Escape($prefix))-(\d+)$" }
$next = 1
if ($existing) {
    $numbers = $existing | ForEach-Object { [int]($_.Name -replace "^$([regex]::Escape($prefix))-", '') }
    $next = ($numbers | Measure-Object -Maximum).Maximum + 1
}
$target = Join-Path $parent "$prefix-$next"

# 3) Frisch publishen – getrennt pro Projekt, damit keine
#    Fassaden-Stub-DLLs (WindowsBase.dll etc.) ueberschrieben werden
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Invoke-Publish 'FrameBouncer/FrameBouncer.csproj'                         (Join-Path $staging 'app')
Invoke-Publish 'FrameBouncer.ElevationHelper/FrameBouncer.ElevationHelper.csproj' (Join-Path $staging 'helper')
Invoke-Publish 'FrameBouncer.Updater/FrameBouncer.Updater.csproj'         (Join-Path $staging 'updater')

# 4) Zielordner erzeugen
New-Item -ItemType Directory -Force -Path $target | Out-Null

# 5) Zusammenfuehren: erst alle App-Dateien, danach NUR die tool-eigenen
#    Dateien von Helper/Updater (nie Laufzeit-DLLs ueberschreiben)
Get-ChildItem (Join-Path $staging 'app') -File | Where-Object {
    $_.Name -notlike 'FrameBouncer.ElevationHelper*' -and
    $_.Name -notlike 'FrameBouncer.Updater*'
} | Copy-Item -Destination $target -Force

Get-ChildItem (Join-Path $staging 'helper') -File | Where-Object {
    $_.Name -like 'FrameBouncer.ElevationHelper*'
} | Copy-Item -Destination $target -Force

Get-ChildItem (Join-Path $staging 'updater') -File | Where-Object {
    $_.Name -like 'FrameBouncer.Updater*'
} | Copy-Item -Destination $target -Force

# 6) Aufraeumen
Remove-Item $staging -Recurse -Force

# 7) Ergebnis pruefen + Versions-Stand schreiben
$exe = Join-Path $target 'FrameBouncer.exe'
if (-not (Test-Path $exe)) { throw 'FrameBouncer.exe fehlt nach dem Build!' }

$v     = (Get-Item $exe).VersionInfo
$stamp = "Teststand $next`r`nVersion: $($v.ProductVersion)`r`nGebaut: $(Get-Date -Format 'dd.MM.yyyy HH:mm')"
Set-Content -Path (Join-Path $target 'VERSION.txt') -Value $stamp -Encoding UTF8

Write-Host ''
Write-Host "OK - Neuer Teststand liegt in: $target" -ForegroundColor Green
Write-Host $stamp
Write-Host ("Helper:      {0}" -f (Test-Path (Join-Path $target 'FrameBouncer.ElevationHelper.exe')))
Write-Host ("Updater:     {0}" -f (Test-Path (Join-Path $target 'FrameBouncer.Updater.exe')))
if ($next -gt 1) {
    Write-Host ''
    Write-Host "Der aeltere Stand ($prefix-$($next - 1)) bleibt unveraendert erhalten." -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Einfach FrameBouncer.exe in diesem Ordner doppelklicken zum Testen.'