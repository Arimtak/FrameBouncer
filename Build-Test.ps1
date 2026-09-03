# Build-Test.ps1
# Baut den NEUESTEN FrameBouncer-Stand als portable SINGLE-EXE (self-contained
# win-x64 Release, Elevation + Update in derselben Datei) und legt sie in einen
# NEUEN, nummerierten Testordner ab. Fruehere Staende bleiben unveraendert:
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

# 3) Portable Single-EXE frisch publishen (self-contained, alles in EINER Datei)
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
Write-Host "Publishe FrameBouncer (single-file) ..." -ForegroundColor Cyan
dotnet publish (Join-Path $repoRoot 'FrameBouncer/FrameBouncer.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true `
    -o $staging --nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Publish fehlgeschlagen!' }

# 4) Zielordner erzeugen + EXE uebernehmen
New-Item -ItemType Directory -Force -Path $target | Out-Null
$exe = Join-Path $staging 'FrameBouncer.exe'
if (-not (Test-Path $exe)) { throw 'FrameBouncer.exe fehlt nach dem Publish!' }
Copy-Item $exe -Destination $target -Force

# 5) Aufraeumen
Remove-Item $staging -Recurse -Force

# 6) Versions-Stand schreiben
$v     = (Get-Item (Join-Path $target 'FrameBouncer.exe')).VersionInfo
$stamp = "Teststand $next`r`nVersion: $($v.ProductVersion)`r`nGebaut: $(Get-Date -Format 'dd.MM.yyyy HH:mm')"
Set-Content -Path (Join-Path $target 'VERSION.txt') -Value $stamp -Encoding UTF8

Write-Host ''
Write-Host "OK - Neuer Teststand liegt in: $target" -ForegroundColor Green
Write-Host $stamp
Write-Host ("Portable Single-EXE: {0}" -f (Test-Path (Join-Path $target 'FrameBouncer.exe')))
if ($next -gt 1) {
    Write-Host ''
    Write-Host "Der aeltere Stand ($prefix-$($next - 1)) bleibt unveraendert erhalten." -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Einfach FrameBouncer.exe doppelklicken zum Testen - alle Daten liegen unter Dokumente\FrameBouncer.'