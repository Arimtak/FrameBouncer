# FrameBouncer – Architektur

Stand: September 2026. Beschreibt die **tatsächliche** Implementierung.

## Überblick

```
App.OnStartup (Composition Root)
│
│   wählt je Verfügbarkeit: echte Implementierung, sonst Dummy
├── IRtssService          → RtssService            | Fallback: DummyRtssService
├── IAfterburnerService   → AfterburnerService     | Fallback: DummyAfterburnerService
├── IProcessService       → ProcessService         | Fallback: DummyProcessService
├── IAutostartService     → RegistryAutostartService (HKCU Run-Key, ohne Admin)
├── IFrameTimeProvider    → RtssFrameTimeProvider  (immer echt, kein Simulations-Fallback)
├── ISettingsService      → JsonSettingsService    (%APPDATA%\FrameBouncer\settings.json)
└── IWindowPickerService  → WindowPickerService    (Fenster per Mausklick)
│
└── MainViewModel ── MainWindow (WPF, Tray, Maus-Hook)
```

## RTSS-Integration

### Lesen (FPS / Frametime)

- Shared Memory `RTSSSharedMemoryV2` (auch `Local\`/`Global\`-Varianten), nur lesend
  (`OpenFileMappingA` + `MapViewOfFile`).
- Header: Signatur `0x52545353` („RTSS“), `AppEntrySize` @8, `AppArrOffset` @12, `AppArrSize` @16.
- App-Entry: `ProcessID` @+0, `Name[260]` @+4, `Time0` @+268, `Time1` @+272, `Frames` @+276,
  `FrameTime` @+280 (Mikrosekunden).
- FPS-Berechnung: bevorzugt `1000000 / FrameTime`, sonst `1000 * Frames / (Time1 - Time0)`.
- `RtssFrameTimeProvider` liest bevorzugt den Eintrag des **Vordergrund-Prozesses** (PID-Match),
  sonst den Eintrag mit der höchsten FPS; hält den letzten gemessenen Wert für max. 4 Ticks
  (~100 ms) als Puffer gegen kurze Aussetzer. Danach liefert er bewusst ein
  `Source = Unavailable`-Sample – **keine Simulation**.
- Die Rohwert-Umrechnung liegt im getesteten `RtssFrameDataParser`: `FrameTime` (µs) > 0 →
  gemessene Frametime (`FT/1000` ms, FPS = 1e6/FT); sonst Fallback `Frames/(Time1−Time0)` →
  abgeleitete Frametime (1000/FPS); beides nicht brauchbar → `Unavailable` (FT280=0 heißt
  nie "0 ms").
- Jedes `FrameTimeSample` trägt `Source` (Measured/Derived/Unavailable) und `ProcessName`
  (gemessener RTSS-Entry) – Grundlage für die ehrliche Anzeige und den Spielwechsel-Reset.

### Monitoring-Anzeige (FPS / Frametime / 1% & 0,1% Low)

- **FPS-Quelle:** RTSS Shared Memory (Vordergrund-Entry bevorzugt), angezeigt als gerundete
  Ganzzahl; ohne gültige Daten `--`.
- **Frametime-Quelle:** bevorzugt die gemessene `dwFrameTime` (µs → ms); nur wenn RTSS keine
  liefert, die abgeleitete `1000/FPS` – in der UI mit `≈` gekennzeichnet. Ohne Daten:
  `nicht verfügbar`.
- **1%-Low-Methode** (identisch zum aggregierten Ansatz gängiger Benchmark-Tools): die letzten
  N Frametimes im Ringpuffer sortieren, `k = max(1, floor(N · 0,01))`, arithmetisches Mittel
  der k langsamsten Frametimes, Low-FPS = `1000 / Mittelwert`. Kein einzelner Minimalwert.
- **0,1%-Low-Methode:** identisch mit `p = 0,001`.
- **Fenster/Ringpuffer:** `LowPercentileCalculator`, Kapazität 10 000 Frametimes (konstanter
  Speicher; ~100 s Geschichte bei 100 FPS). 1%-Low ab 100 Samples, 0,1%-Low erst ab 1000
  Samples – darunter ehrlich `--`. Berechnung 1× pro Sekunde (1-s-Hardware-Tick), nicht im
  25-ms-Frame-Tick; keine LINQ-Ketten, vorallokierter Sortier-Puffer.
- **Spielwechsel:** wechselt der gemessene `ProcessName`, werden Ringpuffer und Anzeigen
  zurückgesetzt (alte Samples gehören zum alten Spiel). Ohne gültige Daten wird der gesamte
  Monitoring-Zustand zurückgesetzt.

### Schreiben (FPS-Limit)

> **Wichtig (empirisch verifiziert):** RTSS liest aktive Limiter-Einstellungen aus
> `<RTSS>\Profiles\<Prozess>.cfg`. `ProfileTemplates\` ist **nur die Vorlage** für neue,
> in der RTSS-GUI erstellte Profile – Einträge dort haben **keinen** Einfluss auf laufende
> Spiele. Ein früherer Build schrieb nur die Vorlage; der Benutzer sah dann "Limit=0" und
> das gewünschte FPS-Limit griff nie.

Zwei ergänzende Wege:

1. **LIVE (sofort wirksam, ohne UAC):** RTSSHooks64.dll — die DLL wird beim App-Start im
   **Hintergrund** vorgeladen (die Initialisierung kann in einer gehookten Umgebung hängen
   und darf den UI-Thread nie blockieren). Im Apply-Pfad werden die Export-Delegates der
   geladenen DLL idempotent gebunden (kein `LoadLibrary` im UI-Thread) und die Sequenz
   `LoadProfile` → `SetProfileProperty("FramerateLimitDenominator", 1)` →
   `SetProfileProperty("FramerateLimit", …)` → `SaveProfile` → `UpdateProfiles` ausgeführt.
   Rückgabewerte werden geprüft; `SaveProfile = false` (ohne Adminrechte normal, da ACL) ist
   kein Fehler — die Persistenz übernimmt Weg 2.

   > **Regression behoben (Live-Pfad war stumm):** Der Hintergrund-Preload setzte nur das
   > Modul-Handle; ein reiner Handle-Check übersprang danach die Export-Bindung → alle
   > Delegates blieben `null` und `SetProfileProperty` lieferte immer `false`. Der Live-Pfad
   > bindet die Delegates jetzt sofort, sobald das Modul geladen ist (`HooksLive` wieder
   > `true`, empirisch verifiziert).
2. **PERSISTENZ:** `<RTSS>\Profiles\<Prozess>.cfg` — Sektion `[Framerate]`, Schlüssel
   `Limit` (`RtssProfileWriter.SetProfileLimit`). Existierende Dateien werden per
   Read-modify-write nur am Limit-Schlüssel geändert (OSD/Statistics/Hooking/Font bleiben
   unangetastet); fehlende Dateien werden als minimale, vollständige Profil-Datei erzeugt
   (inkl. `[Hooking] EnableHooking=1`). `Limit=0` deaktiviert das Limit (RTSS-Konvention).

Anschließend bekommt das RTSS-Fenster (per `EnumWindows` gesucht) `WM_APP + 0x100` gepostet
(Non-blocking via `PostMessage`), damit es die Profile neu lädt.

> **Elevation (aktueller Stand):** Das App-Manifest ist `asInvoker` – die App startet ohne UAC.
> Die INI-Logik liegt in `RtssProfileWriter` und wird direkt versucht. Schlägt der Schreibzugriff
> auf `Profiles\` unter `Program Files (x86)` mit `UnauthorizedAccessException` fehl (normal,
> da ACL-geschützt), startet `RtssService` on-demand **`FrameBouncer.ElevationHelper`**
> (`requireAdministrator`, `Verb = runas`):
> `ElevationHelper.exe [writeLimit|writeTemplate] <installPath> <processName> <targetFps>`
> (ohne Operation = `writeLimit`), Exit-Code 0 = Erfolg, 2 = abgelehnte UAC/Argumente.
> `writeLimit` schreibt das aktive `Profiles\`-Profil, `writeTemplate` nur die GUI-Vorlage
> (Legacy). Der Helper teilt sich `RtssProfileWriter.cs` per Compile-Link
> mit der App – keine duplizierte Logik.
> **Kein UAC-Spam:** Markierte Auto-Apply-Prozesse (`_autoAppliedProcesses`) verhindern,
> dass ein fehlgeschlagener/abgelehnter Schreibvorgang pro laufender Instanz wiederholt
> wird.
>
> **UAC-freier Exit-Reset (empirisch verifiziert, RTSS 7.3.5 / ACL-geschützte `Profiles\`):**
> Die Live-API allein reicht NICHT – `SetProfileProperty(FramerateLimit, n)` wird vom
> RTSS-Server akzeptiert (`true`), aber der Limiter eines laufenden, gehookten Spiels folgt
> der Profil-**Datei**; auch `WM_APP+0x100` ändert daran nichts, und `SaveProfile` scheitert
> ohne Adminrechte (ACL). Deshalb gilt: Der zuverlässige Cap/Reset läuft über die Datei.
> Damit er ohne Dauer-UAC funktioniert, erweitert der **ElevationHelper nach dem ersten
> erfolgreichen `writeLimit` einmalig die ACL** des `Profiles\`-Ordners um `Modify` für den
> aktuellen Benutzer (`icacls … /grant "Benutzer:(OI)(CI)M" /T`, best effort, reversible
> Systemänderung nur an diesem Ordner). Danach schreiben Apply **und** Exit-Reset direkt
> (empirisch: Cap 60 → exakt 60 FPS, Reset → unlimitiert, jeweils ohne Helper/UAC).
> Grenze: Auf Systemen, auf denen weder die ACL gesetzt wurde noch direkte Writes möglich
> sind, fällt der Exit-Reset auf den Helper zurück (eine UAC) – er bleibt damit immer
> korrekt. RTSS ab 7.3.7 wendet geänderte Limits laut Community-Skripts zusätzlich live an;
> auf 7.3.5 ist das nicht der Fall.

## MSI-Afterburner-Integration

- Shared Memory `MAHMSharedMemory` (+ `Local\`/`Global\`), nur lesend.
- Header: Signatur `0x4D41484D` („MAHM“), `HeaderSize`, `EntryCount`, `EntrySize`.
- Entries: `Name[260]` @+0 (ASCII), `Value` (float) @+1300.
- Genutzte Sensoren: `GPU temperature`, `CPU temperature`.

## Monitor-/Refreshrate-Erkennung

- **Quelle:** `EnumDisplayMonitors` + `GetMonitorInfo` + `EnumDisplaySettings`
  (`ENUM_CURRENT_SETTINGS`) – der tatsächlich aktive Windows-Displaymode, KEINE
  EDID-/Namens-Raterei. `dmDisplayFrequency` liefert die reale Bildwiederholrate.
- **Architektur:** `IMonitorInfoService` → `MonitorInfoService`; Datenmodell `MonitorInfo`
  (`DisplayName`, `RefreshRateHz`, `IsAvailable`, `MonitorId`, `IsPrimary`). Fabriken
  (Enumeration + Displaymode-Leser) sind injizierbar → Testbarkeit ohne echten Bildschirm.
- **Zielmonitor:** Fenster des überwachten Prozesses (`MonitorFromWindow`), sonst primärer
  Monitor, niemals ein zufälliger. Fehlt beides → `IsAvailable=false` → UI zeigt „Unbekannt“
  (nie „0 Hz“).
- **Aktualisierung:** beim Start, bei Wechsel des überwachten Prozesses, im 1-s-Hardware-Tick
  mit 10-s-Cache. NIEMALS im 25-ms-Frametiming-Tick. Rein lesend – verändert weder
  Display-Einstellungen, RTSS noch Profile.

## VRR-Erkennung (rein diagnostisch, nur lesend)

- **Quelle:** VESA-EDID-„Display Range Limits“-Deskriptor (Tag `0xFD`) im 128-Byte-Basisblock.
  EDID-Lesen: `EnumDisplayDevicesW` (Geräte-ID) → SetupAPI (`GUID_DEVCLASS_MONITOR`,
  Modell-Token-Abgleich) → Registry-Wert `EDID` unter
  `HKLM\...\Enum\DISPLAY\<instance>\Device Parameters`. Parsing nach VESA-Standard
  (DTD-Slots 54/72/90/108, Header-Signatur `00 FF FF FF FF FF FF 00` + Prüfsumme) in
  `EdidRangeLimitsParser`.
- **Architektur:** `IVrrDetectionService` → `VrrDetectionService`; erweitert `MonitorInfo` um
  `Support`/`State`/`Technology` (Enums, keine freien Strings). EDID-Leser und Support-
  Bewertung sind injizierbare Fabriken → testbar ohne echte Hardware (Tests nutzen
  synthetische EDID-Blöcke).
- **Support-Bewertung (dokumentierte, konservative Heuristik):** `Supported` nur bei
  min ≤ 48 Hz, max ≥ 90 Hz und Spanne ≥ 40 Hz (typische FreeSync-/G-SYNC-Bereiche wie
  40–144, 48–144, 30–144). `NotSupported` nur bei schmaler, hoher Spanne (min ≥ 50,
  max−min < 25, z. B. 56–60). Alles andere – inkl. kaputter/minimaler Angaben – → `Unknown`.
  Es wird nie ein „Supported“ erfunden.
- **Aktiver Status & Technologie — ehrliche Grenze:** Windows stellt KEINE öffentlich
  dokumentierte API für den aktiven VRR-Zustand oder die Technologie (G-SYNC/FreeSync/
  Adaptive Sync) eines Monitors bereit. Beide bleiben deshalb ehrlich `Unknown`; es wird
  niemals aus GPU-Hersteller oder Monitorname geschlossen. „VRR: Unbekannt“ ist ein gültiges
  Ergebnis (Spec Punkt 13), kein Fehler.
- **Zielmonitor:** identisch zur Monitor-Erkennung (Fenster des überwachten Prozesses, sonst
  primärer Monitor) – die bestehende Zuordnung wird wiederverwendet, nicht neu implementiert.
- **Aktualisierung:** am selben Refresh-Pfad wie die Monitor-Erkennung (Start, Prozesswechsel,
  1-s-Hardware-Tick), zusätzlich pro Monitor gecacht (10 s). NIEMALS im 25-ms-Tick.
- **Rein diagnostisch:** kein RTSS-, Profil-, Display- oder Treiber-Schreibvorgang.
- **Bekannte Grenze (auf dem Testsystem belegt):** Der Monitor („A32 V2“) deklariert einen
  intern inkonsistenten Range-Limits-Deskriptor (min 0 Hz / max 48 Hz bei 120-Hz-Betrieb,
  min. horizontale Rate > max. horizontale Rate) → die App zeigt ehrlich „VRR Unbekannt“
  statt eines geratenen Wertes. Das ist gewolltes Verhalten.

## Smart-Cap (rein diagnostische Empfehlung)

- **Formel (dokumentiert):** `RecommendedCap = RefreshRate − Headroom(RefreshRate)` mit
  `Headroom = 3` für RefreshRate ≤ 200 Hz und `Headroom = 4` für &gt; 200 Hz. Begründung:
  3 FPS Reserve halten den Cap sicher innerhalb des variablen VRR-Bereichs (Standard für
  G-SYNC/FreeSync-Engagement: 117/141/162/177 FPS auf 120/144/165/180 Hz); oberhalb von
  200 Hz ist die Framezeit pro Frame kleiner als 5 ms, daher +1 Frame Reserve (236 auf
  240 Hz).
- **Reine Funktion:** `SmartCapCalculator.Calculate(refreshRate, support, state)` →
  `SmartCapResult(HasRecommendation, RecommendedFps, Reason)` — keine Seiteneffekte,
  vollständig außerhalb von XAML/ViewModel, per Unit-Test überprüfbar (Spec Punkte 3/4).
- **VRR-Berücksichtigung (Spec Punkt 5):**
  - VRR **aktiv** → Vorschlag; Grund nennt den aktiven VRR.
  - VRR **unterstützt, Status unbekannt** → vorsichtiger Vorschlag; Grund nennt
    ausdrücklich „VRR-Status unbekannt“, niemals „sicher“.
  - VRR **inaktiv** → KEIN Vorschlag (ein Cap unterhalb der Bildwiederholrate bringt ohne
    aktives VRR keinen Nutzen).
  - VRR **nicht unterstützt** → KEIN Vorschlag (Cap ohne VRR sinnlos).
  - VRR **nicht verfügbar** oder **Refresh unbekannt** → KEIN Vorschlag (Punkt 5.5).
- **Übernahme (Punkt 6):** Der UI-Button „Übernehmen“ setzt AUSSCHLIESSLICH `TargetFps` —
  KEIN RTSS-Write, keine Profiländerung. Erst der bestehende Apply-Button schreibt RTSS und
  persistiert das Profil (gleicher, unveränderter Write-Pfad).
- **Aktualisierung (Punkt 9):** im VRR-/Monitor-Refresh-Pfad (Start, Prozess-/Monitorwechsel,
  10-s-Cache). NIEMALS im 25-ms-Frametiming-Tick.
- **Rein diagnostisch:** niemals automatische Änderung von RTSS, TargetFps, SelectedProcess
  oder SavedProfiles.

## Prozess- & Fenstererkennung

- `ProcessService`: alle Prozesse mit sichtbarem Hauptfenster, ohne bekannte Systemprozesse,
  dedupliziert als `<Prozessname>.exe`.
- `WindowPickerService`: After the user clicks (`WH_MOUSE_LL`-Hook im MainWindow wird
  `WindowFromPoint` → `GA_ROOTOWNER` → PID → Prozessname `.exe` + Fenstertitel aufgelöst).

## FPS-Limiter-Konflikterkennung (diagnostisch, nur lesend)

- **Architektur:** `ILimiterConflictService` → `LimiterDetectionService` (I/O, gecacht,
  im 1-s-Tick auf 10 s gedrosselt) → reiner `ConflictAnalyzer` (testbar, kein I/O).
  Datenmodell: `LimiterState` (Source/Status/LimitFps) + `LimiterConflictResult`
  (HasConflict/EffectiveLimitHint/Message).
- **Erkannte Quellen und deren Verlässlichkeit:**
  - **RTSS** – aus der bestehenden Integration (Verfügbarkeit + aktives Profil-Limit des
    gewählten Prozesses). Status kann Off/On/Unbekannt sein – kein zweiter RTSS-Zugriffspfad.
  - **NVIDIA/AMD** – der GPU-**Hersteller** wird zuverlässig via Registry erkannt
    (`DriverDesc`, einmalig gecacht). Der Limit-Wert wird Per-Game über
    `IDriverLimitProvider` angefragt (nur lesend, nie werfend):
    - `NvidiaDriverLimitProvider` prüft die **verifizierten** NVAPI-Einstiegspunkte
      (`nvapi64.dll` + Export `nvapi_QueryInterface`; `NvAPI_Initialize` = `0x0150E828`,
      `NvAPI_DRS_GetCurrentGlobalProfile` = `0x617BFF9F`, beide community-verifiziert).
      Die restliche DRS-Lese-Kette – Funktions-IDs für `NvAPI_DRS_GetSettings`/
      `GetSetting`, die Setting-ID `NVDRS_FRAME_RATE_LIMITER` und die Struct-Layouts
      `NVDRS_SETTINGS`/`NVDRS_SETTING` – ist nicht aus dem öffentlich verifizierbaren
      SDK belegt → das Limit bleibt ehrlich **Unbekannt** (Spec: „NICHT raten“).
    - `AmdDriverLimitProvider` liefert ehrlich **Unbekannt**: ADLX dokumentiert kein
      FRTC-Lesen, Registry-UMD-Werte wären verbotene Heuristik.
  - **Per-Game & Aktualisierung:** Die aktuell überwachte EXE wird an den Provider
    übergeben; ein Wechsel des Spiels invalidiert den 10-s-Cache sofort (kein
    veralteter Wert für ein anderes Spiel). Ungültige Provider-Daten (0/negativ) werden
    zu **Unbekannt** säubert – „kein Wert gefunden“ ≠ „0 FPS“ ≠ „Aus“.
  - **Kein 25-ms-Zugriff:** Treiber-/V-Sync-Abfragen laufen ausschließlich im 1-s-Hardware-Tick
    (10-s-Cache), nie im 25-ms-Frametiming-Tick.
  - **In-Game-Limiter – Detector-Registry** (`IInGameLimiterDetector`): Es gibt KEINE
    universelle In-Game-Limiter-API; die Erkennung läuft über erweiterbare, read-only
    Detectoren (`CanHandle(GameContext)` / `Detect(GameContext)`, nie werfend) mit
    `GameContext` (Prozessname/PID/Exe-Pfad/Install-Verzeichnis, geliefert vom
    `GameContextProvider` via `Process.GetProcessesByName`). Ohne passenden Detector
    oder Kontext → ehrlich **Unbekannt**, **keine FPS-Heuristik** (FPS/Low/Frametime/
    Refresh/RTSS/NVIDIA/AMD/V-Sync sind NIE Beweis für ein Game-Limit).
    - **Proof-of-Concept: `SourceEngineFpsMaxDetector`** – Source-1-Spiele (Valve).
      Signatur: `GameInfo.txt` im Install-Verzeichnis. Quelle: `fps_max` in
      `cfg\autoexec.cfg` / `cfg\config.cfg` (autoexec hat Vorrang).
      `fps_max 0` → **Aus** (ausdrücklich unbegrenzt), `fps_max n` (n>0) → **Aktiv n FPS**,
      ungültig/negativ/kein Schlüssel → **Unbekannt**, kein `cfg\` → **Nicht verfügbar**.
      Source 2 (`gameinfo.gi`) und alle anderen Spiele → **Unbekannt**.
  - **V-Sync – quellentreu je Ebene** (`LimiterSource.NvidiaVSync` / `AmdVSync` /
    `InGameVSync`; eigener Status `LimiterStatus.Unavailable` für „Quelle nicht verfügbar“):
    - `NvidiaVSyncProvider` prüft die **verifizierten** NVAPI-Einstiegspunkte
      (`nvapi64.dll` + `nvapi_QueryInterface`). Fehlt die DLL → **Nicht verfügbar**;
      ist sie geladen, aber die DRS-Setting-ID `NVDRS_VSYNCMODE` samt Lese-Kette nicht aus
      dem öffentlich verifizierbaren SDK belegt → ehrlich **Unbekannt**.
    - `AmdVSyncProvider` liefert ehrlich **Unbekannt**: ADLX dokumentiert kein
      V-Sync-Lesen, Registry-Werte wären verbotene Heuristik.
    - **In-Game-V-Sync** ohne verifizierbare universelle Konfigurationsquelle → **Unbekannt**.
      Nur der Provider des erkannten GPU-Herstellers wird abgefragt; die Ebenen werden
      getrennt ausgewiesen („NVIDIA V-Sync: aktiv“ ≠ „globaler V-Sync: aktiv“).
    - V-Sync trägt **keinen FPS-Wert** – versehentlich gelieferte Limits werden entfernt;
      fehlende Info wird NIE als "Aus" interpretiert.
- **Konfliktregel:** ≥ 2 FPS-Limiter *zuverlässig aktiv* → Warnung (vorsichtig formuliert:
  "Das tatsächlich wirksame Limit kann abweichen") + niedrigstes bekanntes Limit als
  Diagnosehinweis. **V-Sync ist auf keiner Ebene ein FPS-Limiter**: weder V-Sync allein
  noch RTSS+V-Sync erzeugen einen Konflikt (dafür braucht es zwei sicher aktive FPS-Limits).
  VRR+V-Sync gilt als normale Konfiguration (VRR wird gar nicht als Limiter bewertet).
- **Garantien:** streng nur-lesend (keine Schreibvorgänge an RTSS, Treiber, Windows,
  Spielkonfigurationen), kein automatisches Anwenden des niedrigsten Limits,
  keine Profiländerung, kein Crash bei RTSS-Ausfall.
- **UI:** kompakte Konflikt-Markierung (⚠) in der Statuszeile; Tooltip zeigt alle
  Quellen-Zustände getrennt (z. B. "RTSS: 120 FPS | In-Game: Unbekannt | NVIDIA: Unbekannt |
  In-Game V-Sync: Unbekannt | NVIDIA V-Sync: Unbekannt"); `Unavailable` erscheint als
  "Nicht verfügbar".
- **Bekannte Grenzen:** Ohne die aus dem SDK verifizierbaren DRS-Funktions-/Setting-IDs
  und Struct-Layouts bleiben aktive NVIDIA-/AMD-Treiber-Limiter **und** Treiber-V-Sync-
  Zustände unsichtbar (Status ehrlich **Unbekannt**/„Nicht verfügbar“, kein erfundener
  Konflikt-Alarm). Der NVAPI-Probe (LoadLibrary `nvapi64.dll` +
  `nvapi_QueryInterface`-Auflösung) ist rein lesend – keine DLL-Injection, keine Aufrufe
  mit nicht verifizierten IDs. In-Game-Erkennung ist nur für Spiele möglich, für die ein
  Detector mit verifizierter Quelle existiert (aktuell: Source-1-`fps_max`); alle anderen
  Spiele, Source-2-Spiele und Spiele ohne auffindbaren `fps_max`-Schlüssel bleiben ehrlich
  **Unbekannt**. Der Kontextzugriff (`MainModule.FileName`) kann bei fremder Elevation
  scheitern → dann ebenfalls **Unbekannt**. In-Game-V-Sync wird grundsätzlich nicht erkannt;
  es gibt keine universelle Windows-/Treiber-API für den tatsächlichen V-Sync-Zustand eines
  Spiels. Die Erkennung ist konservativ: Sie meldet nur belegbare Konflikte und erfindet keine.

## Profil-Backup & Restore

- **Was gesichert wird:** ausschließlich die von FrameBouncer verwalteten `SavedProfiles`
  (ProcessName, TargetFps, Enabled, Zeitstempel). Erkannte Prozesse landen **nie** im Backup
  (Detection ≠ Saved), fremde RTSS-Konfiguration wird nicht behauptet, da sie nicht gelesen wird.
- **Format:** JSON, `formatVersion` 1, camelCase-Schlüssel (`formatVersion`, `createdAt`,
  `appVersion`, `profiles[]` mit `processName`/`targetFps`/`enabled`/`createdUtc`/`updatedUtc`).
  Ablage: `%APPDATA%\FrameBouncer\Backups\FrameBouncer-Profiles-<Zeitstempel>.json` oder ein
  beliebiger Benutzerpfad via SaveFileDialog. Dateinamen sind pro Aufruf eindeutig
  (Sekundenstempel + ` (2)`-Suffix bei Kollision).
- **Backup nur explizit:** App-Start, Prozess-Erkennung, Auswahl, Auto-Apply und normales Apply
  erzeugen **niemals** Backup-Dateien – nur der "⭳ Backup"-Button tut das.
- **Architektur:** `IProfileBackupService`/`ProfileBackupService` (Dateispeicherung + Restore),
  `BackupValidator` (reine Validierung: JSON-Syntax, unterstützte `formatVersion`, gültiger
  EXE-Name ohne Pfad-/Dateisystem-Zeichen, FPS 1–1000, keine doppelten Profile),
  `IBackupFilePicker`/`BackupFilePicker` (Dialoge), `AtomicFile` (Temp-Datei + `File.Replace`).
  Keine Backup-Logik in Code-Behind.
- **Restore-Ablauf:** Datei wählen → validieren (ungültige Dateien werden mit deutscher
  Fehlermeldung abgelehnt: "ungültiges JSON-Format", "Nicht unterstützte Backup-Version: N",
  "ungültiger FPS-Wert", "doppeltes Profil", …) → Bestätigungsdialog mit Zusammenfassung →
  **Safety-Backup** der aktuellen Profile → Übernahme → `settings.json` aktualisieren →
  UI aktualisieren (Prozessliste frisch, Auswahl bleibt erhalten). Schlägt der Restore fehl,
  bleiben die aktuellen Profile unverändert (Safety-Backup existiert zusätzlich).
- **Kein RTSS-Write durch Restore:** Es wird nur Persistenz + UI aktualisiert. Laufende Spiele
  werden nicht umkonfiguriert; die RTSS-Anwendung erfolgt weiterhin ausschließlich über die
  bestehende Apply-/Auto-Apply-Logik beim passenden Spielstart.
- **Atomisches Speichern:** Sowohl Backup-Dateien als auch `settings.json` werden über
  `AtomicFile` geschrieben (vollständige Temp-Datei im selben Verzeichnis, dann Replace).
  Ein Abbruch während des Schreibens hinterlässt niemals ein halbes JSON.
- **Bekannte Grenzen:** RTSS-eigene Profile-Konfiguration (RTSS-Installationsordner) ist nicht
  Teil des Backups – FrameBouncer sichert nur seine eigene Persistenz. Backups einer zukünftigen
  `formatVersion` > 1 werden abgelehnt (Version wird in der Meldung genannt), nicht stillschweigend
  geladen.

## Autostart & Mitstarten von Tools

- **App-Autostart:** `RegistryAutostartService` verwaltet `HKCU\Software\Microsoft\Windows\
  CurrentVersion\Run` → Wert `FrameBouncer` = Pfad der aktuellen EXE. Kein Admin nötig.
- **RTSS/Afterburner mitstarten:** Opt-in (`StartRtssWithApp` / `StartAfterburnerWithApp` in den
  Einstellungen). Beim Start wird geprüft, ob der Prozess bereits läuft; falls nicht, wird er
  per `Process.Start` (UseShellExecute, **ohne** `Verb = runas`) im Hintergrund gestartet –
  kein UI-Block und keine UAC-Aufforderung. Findet sich die EXE am Standardpfad nicht, passiert
  nichts (nur Debug-Log).

## Spielprofile & Auto-Apply

- `GameProfile` (Models): `ProcessName` (eindeutiger Schlüssel, mit `.exe`), `TargetFps`,
  `IsEnabled`, `CreatedUtc`, `UpdatedUtc`. Persistiert in `AppSettings.SavedProfiles`.
- **Manuelles Apply** (`ApplyFpsLimit`): RTSS-Limit auf `SelectedProcess` setzen +
  `UpsertProfile(SelectedProcess, TargetFps)` (bestehendes Profil wird aktualisiert, Apply
  aktiviert es). Die Erkennungsliste bleibt davon unberührt.
- **Auto-Apply** (`OnProcessRefreshTick` → `AutoApplyNewProcesses`, alle 3 s): für jeden
  neu erkannten sichtbaren Prozess prüfen, ob ein **aktiviertes** Profil existiert →
  ja: `SetFpsLimitViaRtss(exe, profile.TargetFps)`, nein: nichts tun. Nichts wird dabei
  gespeichert. Pro EXE nur ein Write pro laufender Instanz (`_autoAppliedProcesses`, reiner
  Runtime-State, wird nie persistiert); EXEs, die nicht mehr laufen, werden wieder
  vergessen, damit ein späterer Spielstart erneut appliziert. Fehlgeschlagene Versuche
  werden für dieselbe Instanz ebenfalls nicht wiederholt (kein UAC-Spam); der Status
  ("Auto-Apply: …" / "Auto-Apply fehlgeschlagen: …") bleibt sichtbar, bis der nächste
  Auto-Apply sie überschreibt.
- **Startup-Re-Apply** (`ApplyEnabledProfilesForRunningProcesses`): nach App-Neustart werden
  laufende Spiele mit aktivem Profil sofort begrenzt.
- **Exit-Reset** (`ResetFpsLimit`, beim echten Beenden über `CloseApp`/Fenster-`Closing`):
  ALLE in dieser Session angewendeten Limits werden auf 0 zurückgesetzt – manuelles Apply UND
  Auto-Apply, unabhängig vom Frame-Timer (der bei Auto-Apply nicht läuft). Grund: RTSS erzwingt
  das persistierte `Profiles\<exe>.cfg` auch ohne FrameBouncer weiter; nach dem Schließen sollen
  Spiele wieder unlimitiert laufen. Tracking nur im Runtime-State `_limitsAppliedThisSession`
  (nie persistiert); jeder Reset genau einmal je EXE, Fehler einzelner EXEs blockieren das
  Beenden nicht. Der Update-Ablauf (`RequestForceExit`) überspringt den Reset bewusst, weil die
  neue App-Instanz die Profile nach dem Neustart wieder anwendet. Dank der einmaligen
  ACL-Erweiterung (siehe „Schreiben (FPS-Limit)“) läuft der Reset nach dem ersten Apply ohne
  UAC direkt über die Datei – live verifiziert (Cap 60 → Reset → Spiel wieder unlimitiert).
- **Legacy-Migration** (`LoadProfiles`): alte `SavedProcesses`-Einträge (nur EXE-Namen ohne
  FPS) werden als **deaktivierte** Profile übernommen – kein ungewolltes Limitieren;
  ein Apply reaktiviert sie.

## Einstellungen

- Datei: `%APPDATA%\FrameBouncer\settings.json` (Migration von der alten Position neben der EXE
  erfolgt einmalig beim Laden).
- Persistiert werden: `TargetFps`, `SelectedProcess`, `IsTopmost`, `IsAutostartEnabled`,
  `StartRtssWithApp`, `StartAfterburnerWithApp`, `SavedProfiles` (neu; `SavedProcesses` bleibt
  nur als Legacy-Feld für die Migration erhalten und wird nicht mehr befüllt).
- **Profil-Trennung** (wichtig, durch Tests abgesichert): Nur explizit angewendete Prozesse
  („Apply“) landen in `SavedProfiles`. Die bloße Prozess-Erkennung oder Fenster-Auswahl
  speichert nichts.

## UI-Zyklen (MainViewModel)

| Timer | Intervall | Aufgabe |
|-------|-----------|---------|
| `_frameTimer` | 25 ms | Frametime-Sample ziehen, Ringpuffer (120), Chart + Y-Skalierung (EMA) |
| `_hardwareTimer` | 1 s | GPU/CPU-Temperatur, RTSS/Afterburner-Verbindungsstatus |
| `_processRefreshTimer` | 3 s | Prozessliste auffrischen (gespeicherte Profile bleiben immer erhalten) |

## GitHub-Release & sicherer Updater

- **Updatequelle (Punkt 2):** GitHub Releases als einzige offizielle Quelle – kein eigener Server.
  `UpdateConfiguration` bündelt Owner/Repository/Channel an GENAU EINER Stelle (aktuell
  Platzhalter „FrameBouncer/FrameBouncer“ – vor dem ersten Release die echten Daten eintragen).
  API: `GET https://api.github.com/repos/{owner}/{repo}/releases/latest` (nur HTTPS, Punkt 19;
  TLS-Zertifikatsprüfung bleibt aktiv).
- **Version (Punkt 3):** Eine zentrale Quelle – `<Version>1.0.0</Version>` im csproj erzeugt
  Assembly-/File-/Produktversion identisch; `AppVersion.Current` liest sie für den Update-Checker;
  das Release-Asset heißt `FrameBouncer-vX.Y.Z-win-x64.zip` (gleiche Version).
- **Update-Check (Punkt 4/5/17):** `IGitHubReleaseService` → `GitHubReleaseService` verarbeitet
  HTTP 200/404/403/429/5xx, Netzwerkfehler und ungültiges JSON als Status (nie werfend).
  Prereleases werden ignoriert, Downgrades nie angeboten. Manuell per „⟳ Updates“-Button (ohne
  Cooldown); beim App-Start automatisch, aber **max. 1×/24 h** (Cooldown in `settings.json`).
- **Download (Punkt 7/19):** `IUpdateDownloader` → `UpdateDownloader` lädt zip + `.sha256`
  ausschließlich über HTTPS nach `%LOCALAPPDATA%\FrameBouncer\Updates`.
- **Verifikation (Punkt 8/9):** `IUpdateVerifier` → `UpdateVerifier` prüft SHA-256 gegen die
  authentifizierte Release-Metadaten-Datei (`.sha256`-Asset desselben Releases) – niemals gegen
  eingebettete Hashes. **Keine digitale Code-Signatur vorhanden**: `SignatureValidated` ist immer
  false; die Meldung unterscheidet ehrlich „Hash validiert“ vs. „Signatur validiert“ (Punkt 9).
- **Separater Updater (Punkt 10/14/15/24):** `IUpdateInstaller` startet `FrameBouncer.Updater.exe`
  (eigenes Projekt, liegt neben der App; wird wie der ElevationHelper per Build-**und**
  Publish-Target mitkopiert). Ablauf im `UpdateInstallerCore` (testbar, in Tests direkt
  kompiliert): warten, bis der Prozess beendet UND die EXE nicht mehr gesperrt ist → Paket
  validieren + entpacken (Path-Traversal-Schutz: keine `..`, keine absoluten/UNC-Pfade,
  Punkt 25) → Backup der betroffenen Dateien → atomarer Ersatz pro Datei (Temp + `File.Replace`,
  Punkt 12) → App-Neustart → Start-Überwachung; bei Fehler **Rollback** (Punkt 13, auch wenn die
  neue Version nicht startet). Der Updater startet asInvoker und fordert **nur bei Bedarf**
  (z. B. Program Files) einmalig erhöhte Rechte an – kein Dauer-Admin.
- **Nur bekannte Programmbestandteile (Punkt 11/26/29):** Ersetzt werden ausschließlich Dateien,
  die im Release-Paket enthalten sind (Whitelist). `settings.json` liegt in `%APPDATA%` und wird
  nie angefasst; Backups/Logs/Benutzerdaten bleiben unberührt; keine Registry-/Treiber-/RTSS-/
  Afterburner-/Spieleinstellungsänderungen. Die App selbst überschreibt sich nie (sie beendet
  sich nach dem Updater-Start; `RequestForceExit` umgeht dabei auch den Tray-Modus).
- **Offline (Punkt 18):** Ohne Internet funktionieren RTSS, Profile, Monitoring und alle
  Kernfunktionen normal; nur der Checker zeigt „Keine Internetverbindung.“/„Update-Prüfung
  nicht möglich.“.
- **UI (Punkt 6/28):** „⟳ Updates“-Button im Header; bei verfügbarem Update erscheint „⬇ Update“;
  Status in der Statuszeile („Neue Version verfügbar: vX.Y.Z“, „Du verwendest die neueste
  Version.“, „Update konnte nicht verifiziert werden.“, …) – keine Stacktraces.
- **Release-Workflow (Punkt 22):** `.github/workflows/release.yml` – Tag `vX.Y.Z` → Restore →
  Build → **Test (muss grün sein)** → self-contained Publish (win-x64) von App, ElevationHelper
  und Updater → Merge → `FrameBouncer-vX.Y.Z-win-x64.zip` + `.sha256` → GitHub-Release.

## Tests

`FrameBouncer.Tests` (xUnit, in der Solution enthalten) deckt ab:

- Prozess-Filterung (Erkennen, Verschwinden, Auswahl-Erhalt) – `ProcessFilteringTests`
- Profil-Trennung (Erkennen ≠ Speichern, nur „Apply“ persistiert) – `ProfileSeparationTests`
- Fenster-Picker-Flows inkl. Abbruch – `WindowPickerTests`

## Kompatibilität, Abhängigkeiten & bekannte Grenzen

- **RTSS (Shared Memory, `RTSSSharedMemoryV2`):** Die Signatur (`0x52545353`) wird strikt
  geprüft; Layout-Felder (Entry-Größe/-Offset/-Anzahl) kommen aus dem Header selbst und werden
  nicht blind als feste Zahl angenommen (`RtssSharedMemoryHeader`). Die Versionsnummer (Offset 4)
  wird gelesen und diagnostisch erfasst, aber **nicht** als exakte Zahl gegated: Eine gültige,
  nur unbekannte RTSS-Version wird nicht fälschlich als „nicht unterstützt“ abgelehnt. Es gibt
  keine öffentliche, verifizierte RTSS-Versionstabelle, daher ist die Kompatibilitätsgarantie
  „V2-Layout + Signatur“, nicht eine bestimmte Versionsnummer. Ein RTSS-Ausfall (Shared Memory
  nicht vorhanden / nicht lesbar) führt nie zu einem Crash – Monitoring und UI zeigen dann
  ehrlich `--` bzw. „nicht verfügbar“.
- **MSI Afterburner (MAHM Shared Memory):** Die Temperatur-Getter liefern `int?` – `null` heißt
  „Sensor nicht verfügbar“. Fehlende Sensoren oder ein gestoppter Afterburner werden als `--`
  angezeigt, **niemals** als `0 °C`. Der Dummy-Fallback (`DummyAfterburnerService`) meldet
  `IsAfterburnerAvailable() == false` und `null`-Temperaturen (keine erfundenen Werte).
- **ElevationHelper:** Wird per `ProjectReference` (`ReferenceOutputAssembly=false`) + Post-Build
  `CopyElevationHelper`-Target **neben** die App-EXE kopiert (Debug **und** Release, inkl. `dotnet
  publish`). `RtssService.FindElevationHelper` findet ihn dort zuverlässig; die alte
  Debug-Pfad-Hebelleiste entfällt damit praktisch, bleibt aber als Fallback erhalten.
- **Laufzeit / Release-Paket (Punkt 23):** Debug-/Dev-Builds sind framework-dependent (benötigen
  die .NET 8 Desktop Runtime). Das **Release-Paket** des Workflows ist **self-contained win-x64**
  (App + ElevationHelper + Updater, ~160 MB) und läuft ohne manuelle Runtime-Installation;
  die größere Paketgröße ist bewusst akzeptiert und dokumentiert.
- **Nicht implementierte Funktionen (ehrlich):** Es gibt keine verifizierte Quelle für den
  **aktiven VRR-Status** und die **VRR-Technologie** (G-SYNC/FreeSync) — sie bleiben ehrlich
  „Unbekannt“ (siehe VRR-Abschnitt). VRR taucht im Limiter-Modell nur als Konzept auf und wird
  bewusst nicht als Limiter-Konflikt gewertet (VRR + V-Sync ist eine normale Konfiguration).
  Es wird hier nichts als fertig dokumentiert, was nicht implementiert ist.
- **Limiter-Konflikte:** Nur belegbare Quellen (RTSS-Status) werden erkannt; NVIDIA/AMD-Konkret-
  Limits bleiben ehrlich **Unbekannt**; In-Game-Limiter nur über verifizierte Detectoren
  (aktuell Source-1-`fps_max`), sonst **Unbekannt**; V-Sync je Ebene (siehe Abschnitt oben).
- **Testabdeckung:** Kompatibilitäts-/Stabilitäts-Checks im `HardeningTests` (fehlender Sensor →
  `--`, Dummy lügt nicht, Header-Signatur/-Version).
