# FrameBouncer (WPF / .NET 8 / MVVM)

> 🎮 **Für Freunde & Tester – kurz erklärt:** FrameBouncer ist ein kleines Windows-Programm,
> mit dem du die FPS (Bilder pro Sekunde) deiner Spiele fest begrenzen kannst – z. B. auf
> 60, 120 oder 144 FPS – damit dein PC ruhiger läuft, kühler bleibt und weniger Strom
> verbraucht. Es nutzt dafür **RTSS** (RivaTuner Statistics Server), das viele Gamer eh
> schon installiert haben.
>
> Wie du es bedienst, steht unten unter **▶ So benutzt du FrameBouncer**.

---

## ▶ So benutzt du FrameBouncer (Kurzanleitung)

### 1. Installieren (einmalig)

1. Lade die neueste Version von den **GitHub Releases** herunter:
   `FrameBouncer-vX.Y.Z-win-x64.zip`.
2. Entpacke das ZIP in einen beliebigen Ordner (z. B. `D:\Tools\FrameBouncer`).
3. **Wichtig:** Installiere und starte **RTSS** (gratis unter guru3d.com, „RivaTuner
   Statistics Server") – ohne RTSS kann FrameBouncer kein FPS-Limit setzen.
4. Starte `FrameBouncer.exe`. Keine Installation nötig, das Programm ist portabel.

> Beim allerersten „Apply“ kann einmal ein **Windows-Sicherheitsfenster (UAC)** erscheinen.
> Das ist normal: FrameBouncer darf dann einmalig die RTSS-Profil-Datei schreiben.
> Danach fragt es **nie wieder** – auch beim Beenden nicht.

### 2. Ein Spiel begrenzen (in 10 Sekunden)

1. **Spiel auswählen:** Klappe die Liste oben auf – dort stehen alle gerade sichtbaren
   Programme/Spiele. Wähle dein Spiel aus (oder klicke auf **Fenster auswählen** und dann
   ins Spiel hinein).
2. **FPS einstellen:** Tippe den Wert ein (z. B. `60`) oder klicke eine Schnellwahl
   (30 / 60 / 120 / 144).
3. **Apply drücken** – fertig. Das Spiel ist jetzt auf diese FPS begrenzt und das Profil
   wird **gespeichert**.

### 3. Was automatisch passiert

- **Profil merken:** Für jede EXE (z. B. `Cyberpunk2077.exe`) wird gespeichert, welches
  FPS-Limit du eingestellt hast. **Beim nächsten Start des Spiels wird das Limit automatisch
  wieder angewendet** – du musst nichts mehr tun.
- **Beim Beenden:** Schließt du FrameBouncer, werden alle gesetzten Limits wieder
  aufgehoben – deine Spiele laufen danach wieder normal (unbegrenzt).
- **Monitoring:** Unten siehst du live FPS, Frametime, 1%-/0,1%-Low und (falls
  MSI Afterburner läuft) GPU-/CPU-Temperatur.
- **Keine Sorge:** FrameBouncer verändert keine Spieldateien, keine Treiber-Einstellungen
  und nichts an deinem System – es schreibt nur die RTSS-Profil-Datei.

### 4. Nützliches im Überblick

| Symbol / Button | Bedeutung |
|---|---|
| 📌 **Pin** | Fenster immer im Vordergrund halten |
| **Autostart** | FrameBouncer startet automatisch mit Windows |
| 🔔 **Tray** | Beim Schließen (✕) im Infobereich weiterlaufen |
| ⭳ **Backup** / ⭱ **Restore** | Gespeicherte Profile sichern bzw. wiederherstellen |
| ⟳ **Updates** | Nach einer neuen Version suchen (GitHub) |
| **Smart-Cap** (Vorschlag) | Empfehlung neben „Apply“ (z. B. 117 FPS auf 120-Hz-Monitor);
  „Übernehmen“ setzt nur das Feld, „Apply“ schreibt es |

### 5. Deinstallieren

Einfach den Ordner löschen. In `%APPDATA%\FrameBouncer` liegen nur deine Einstellungen
(`settings.json`) – die kannst du auch löschen, dann startet FrameBouncer frisch.

---

## Für Entwickler & Details

Minimalistisches Windows-Utility für FPS-Limitierung, Frametime-Monitoring und Temperaturüberwachung
über **RTSS** (RivaTuner Statistics Server) und **MSI Afterburner**.

## Anforderungen

- .NET 8.0 SDK (Windows) zum Bauen; **.NET 8 Desktop Runtime** zum Ausführen (framework-dependent)
- RTSS und/oder MSI Afterburner für echte Daten. Ohne RTSS zeigt das Monitoring ehrlich `--` bzw.
  „nicht verfügbar“; ohne Afterburner/fehlende Sensoren wird die Temperatur als `--` angezeigt
  (niemals `0 °C`). Die Kernoberfläche, Prozessliste und Profil-/Backup-Funktionen laufen auch
  ohne beide.

## Kompatibilität & Grenzen

- **RTSS:** Shared-Memory-Signatur wird geprüft; Layout kommt aus dem Header (keine blinden
  festen Offsets); die Versionsnummer wird gelesen, aber nicht auf eine exakte Zahl gegated
  (unbekannte Versionen werden nicht fälschlich abgelehnt – es gibt keine verifizierte
  RTSS-Versionstabelle). Ein RTSS-Ausfall crasht nicht.
- **Afterburner-Sensoren:** fehlende Sensoren → `--`, nicht `0 °C`.
- **ElevationHelper:** liegt dank Build-Referenz **neben** `FrameBouncer.exe` (Debug/Release/publish).
- **Nicht implementiert (ehrlich):** Der aktive VRR-Status und die VRR-Technologie
  (G-SYNC/FreeSync) sind über keine öffentliche Windows-API auslesbar → ehrlich „Unbekannt“.
  Details: `docs/architecture.md`.

## Projektstruktur

```text
FrameBouncer.sln
├── FrameBouncer/                     # WPF-Anwendung (net8.0-windows, asInvoker)
├── FrameBouncer.ElevationHelper/     # Konsolen-EXE, requireAdministrator:
│                                     #   schreibt RTSS-Profile on-demand elevated
│   ├── App.xaml / App.xaml.cs        # Composition Root: echte Services mit Dummy-Fallback
│   ├── MainWindow.xaml / .cs         # UI, Tray-Icon, Fenster-Picker (Low-Level-Maus-Hook)
│   ├── MainViewModel.cs              # ViewModel (Ringpuffer, OxyPlot-Chart, Timer-Zyklen)
│   ├── RelayCommand.cs
│   ├── Converters/                   # BooleanToBrush, BooleanToVisibility
│   ├── Models/
│   │   ├── AppSettings.cs            # settings.json-Modell (inkl. Start-Rtss/Afterburner-Flags)
│   │   ├── GameProfile.cs            # Spielprofil: EXE → TargetFps + Enabled
│   │   └── FrameTimeSample.cs
│   └── Services/
│       ├── IRtssService.cs           # RtssService: RTSSSharedMemoryV2 lesen (Signatur 0x52545353),
│       │                             #   FPS-Limit live via RTSSHooks64.dll + persistent via
│       │                             #   Profiles/<Prozess>.cfg (aktiv) / ProfileTemplates (Vorlage)
│       ├── IAfterburnerService.cs    # AfterburnerService: MAHMSharedMemory (GPU/CPU-Temperatur)
│       ├── IProcessService.cs        # ProcessService: sichtbare Fenster-Prozesse
│       ├── IAutostartService.cs      # RegistryAutostartService: HKCU\...\Run-Key (ohne Admin)
│       ├── IFrameTimeProvider.cs     # RtssFrameTimeProvider (echt) / SimulatedFrameTimeProvider
│       ├── ISettingsService.cs       # JsonSettingsService: %APPDATA%\FrameBouncer\settings.json
│       ├── IWindowPickerService.cs   # WindowPickerService: Fenster per Klick wählen
│       └── NativeMethods.cs          # Win32-P/Invoke (Shared Memory, User32, RTSSHooks)
└── FrameBouncer.Tests/               # xUnit-Tests (Prozess-Filterung, Profil-Trennung, Picker)
```

## Kompilieren & Starten

```bash
# Aus dem Repo-Root (Solution enthält App + Tests)
dotnet build FrameBouncer.sln
dotnet test

# App starten
dotnet run --project FrameBouncer
```

> **Administratorrechte:** Die App selbst läuft **ohne** Elevation (`asInvoker`) – `dotnet run`
> funktioniert direkt. Das FPS-Limit wird über die RTSSHooks64.dll (live) **und** die
> Profil-Datei `Profiles\<Prozess>.cfg` (der aktive RTSS-Profilspeicher – `ProfileTemplates\`
> ist nur die GUI-Vorlage ohne Wirkung auf laufende Spiele) gesetzt. Schlägt der direkte
> Datei-Zugriff fehl (ACL unter `Program Files (x86)`), startet die App on-demand den
> elevierten **`FrameBouncer.ElevationHelper`**. Dieser schreibt einmalig und erweitert
> anschließend die ACL des `Profiles\`-Ordners für den aktuellen Benutzer – danach laufen
> **Apply und der Exit-Reset (Spiel wieder unlimitiert nach dem Schließen) ohne weitere
> UAC**. Beim Ablehnen der UAC wird der Vorgang sauber abgebrochen, kein UAC-Spam.
> Grenzen: Die Live-RTSSHooks-API allein kann auf RTSS ≤ 7.3.6 kein laufendes Spiel begrenzen
> (der Limiter folgt der Profil-Datei); Details in `docs/architecture.md`.

## Einstellungen & Autostart

- **settings.json** liegt in `%APPDATA%\FrameBouncer\` (Migration von der alten Position neben
  der EXE erfolgt automatisch beim ersten Start).
- **Autostart** wird als echter `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`-Eintrag
  gesetzt – pro Benutzer, ohne Adminrechte.
- **RTSS/Afterburner mitstarten:** Opt-in-Flags in den Einstellungen (`StartRtssWithApp`,
  `StartAfterburnerWithApp`). Beim App-Start werden die Tools dann im Hintergrund gestartet –
  ohne UAC-Prompt und ohne die UI zu blockieren.

## Spielprofile & Auto-Apply

- Ein **Profil** (`GameProfile`) gehört eindeutig zu einer EXE: `ProcessName`, `TargetFps`,
  `IsEnabled` (plus Zeitstempel). Persistiert in `SavedProfiles` in der `settings.json`.
- **Profil entsteht nur durch „Apply“:** Prozess wählen → FPS einstellen → Apply setzt das
  RTSS-Limit auf genau dieser EXE und speichert/aktualisiert (Upsert) genau dieses Profil.
  Ein Apply aktiviert das Profil jeweils.
- **Auto-Apply beim Spielstart:** Wird ein neuer sichtbarer Prozess erkannt (3-s-Zyklus),
  prüft die App, ob für die EXE ein **aktiviertes** Profil existiert, und setzt dessen
  FPS-Limit automatisch über RTSS. Ohne Profil oder mit deaktiviertem Profil passiert nichts.
  Pro laufender Instanz wird nur einmal geschrieben (auch bei Fehlern nicht wiederholt –
  kein UAC-Spam); nach Spiel-Neustart greift es erneut. Fehlschläge erscheinen als Status
  ("Auto-Apply fehlgeschlagen: …") und blockieren andere Spiele nicht.
- **App-Neustart:** Bereits laufende Spiele mit aktivem Profil werden beim Start sofort
  begrenzt.
- **Strenge Trennung:** `Processes` = aktuell erkannte sichtbare Anwendungen;
  `SavedProfiles` = explizit gespeicherte Benutzerprofile. Die bloße Erkennung oder Auswahl
  speichert niemals ein Profil. Alte Einträge unter `SavedProcesses` (ohne FPS) werden als
  **deaktivierte** Profile migriert, damit nichts ungewollt limitiert wird.

## Architektur & Integrationen

- **RTSS**: Live-FPS & Frametime aus `RTSSSharedMemoryV2`; FPS-Limit via `RTSSHooks64.dll`
  (`LoadProfile`/`SetProfileProperty`/`SaveProfile`) und Profil-Dateien unter `ProfileTemplates\`.
  Direktes Schreiben, wenn die Rechte ausreichen – sonst über den elevierten
  `FrameBouncer.ElevationHelper`. Die geteilte INI-Logik liegt in `RtssProfileWriter.cs`.
  Siehe `docs/architecture.md`.
- **MSI Afterburner**: GPU- & CPU-Temperaturen via `MAHMSharedMemory`.
- **Monitor-/Refreshrate**: echte Windows-Displaymode-Erkennung (`EnumDisplaySettings`),
  Zielmonitor = Fenster des überwachten Prozesses, sonst primär; ohne gültigen Wert ehrlich
  „Unbekannt“ (nie `0 Hz`). Rein diagnostisch. Details: `docs/architecture.md`.
- **VRR-Erkennung**: VRR-Unterstützung aus dem VESA-EDID-Range-Limits-Deskriptor (SetupAPI +
  Registry), Statuszeile „VRR Unterstützt / Nicht unterstützt / Unbekannt / Nicht verfügbar“.
  Aktiver Status & Technologie (G-SYNC/FreeSync) sind über keine öffentliche Windows-API
  auslesbar → ehrlich „Unbekannt“, nie geraten. 10-s-Cache, nie im 25-ms-Tick. Nur lesend.
- **Smart-Cap**: rein diagnostischer FPS-Cap-Vorschlag neben dem Apply-Button
  (`RefreshRate − Headroom`; Headroom 3 bei ≤ 200 Hz, 4 darüber, z. B. 117 auf 120 Hz,
  177 auf 180 Hz). „Übernehmen“ setzt **nur** das FPS-Feld — erst „Apply“ schreibt RTSS.
  Bei VRR inaktiv/nicht unterstützt/nicht verfügbar oder unbekannter Refresh-Rate ehrlich
  kein Vorschlag.
- **Entkoppeltes Frametime-System**: `IFrameTimeProvider` speist einen Ringpuffer
  (120 Samples) mit stabilisierter, automatisch skaliertem Y-Achse in OxyPlot.
- **Monitoring-Anzeige**: FPS als Ganzzahl (`--` ohne Daten), Frametime bevorzugt **gemessen**
  (RTSS `dwFrameTime`, µs → ms) und sonst als `≈`-markierte Berechnung aus FPS; ohne gültige
  Werte ehrlich `nicht verfügbar`. **1%-/0,1%-Low** werden im 1-s-Tick aus dem RTSS-Frame-Fenster
  berechnet (Mittelwert der k langsamsten Frametimes, `k = max(1, floor(N·p))`; Ringpuffer mit
  10 000 Samples; 1% Low ab 100, 0,1% Low ab 1000 Samples, sonst `--`). Spielwechsel leeren die
  Historie – keine Vermischung zwischen Spielen. Details: `docs/architecture.md`.
- **Limiter-Konflikterkennung (diagnostisch)**: erkennt, wenn mehrere Systeme gleichzeitig
  die FPS begrenzen wollen (z. B. RTSS + NVIDIA "Max Frame Rate"). Konfliktlogik als reine,
  getestete Funktion (`ConflictAnalyzer`); Erkennung nur lesend – es werden **niemals**
  Treiber-, RTSS- oder Spieleinstellungen verändert. NVIDIA/AMD-Treiber-Limits werden
  **per Spiel** über `IDriverLimitProvider` angefragt (nur lesend); der NVAPI-Einstieg
  (`nvapi64.dll` + `nvapi_QueryInterface`) ist verifiziert, aber die DRS-Lese-Kette
  (Setting-ID + Struct-Layouts) nicht aus dem öffentlichen SDK belegt → Limit ehrlich
  "Unbekannt", nie geraten. **V-Sync wird quellentreu je Ebene** erkannt (In-Game /
  NVIDIA / AMD): `NvidiaVSyncProvider` nutzt nur die verifizierten NVAPI-Einstiegspunkte
  (fehlende DLL → "Nicht verfügbar", belegbarer Wert fehlt → "Unbekannt"), AMD und
  In-Game ohne verifizierbare Quelle → "Unbekannt". V-Sync ist auf keiner Ebene ein
  FPS-Limiter (RTSS + V-Sync erzeugt keinen Konflikt) und trägt keinen FPS-Wert.
  **In-Game-Limiter** über eine erweiterbare Detector-Registry (`IInGameLimiterDetector`, nur
  lesend) — aktuell **Source-Engine** (`fps_max` in `cfg\autoexec.cfg`/`config.cfg`, Signatur
  `GameInfo.txt`): `fps_max 0` → "Aus", gültiger Wert → Limit, ungültig/fehlend → "Unbekannt".
  Es gibt **keine universelle In-Game-Limiter-API** — ohne verifizierte Quelle bleibt alles
  "Unbekannt", nie geraten und nie als "Aus" interpretiert. Details und
  Erkennungsgrenzen: `docs/architecture.md`.
- **Profil-Backup & Restore**: explizite Buttons ("⭳ Backup" / "⭱ Restore") sichern die
  SavedProfiles als versionierte JSON-Datei (`formatVersion` 1, camelCase-Schlüssel) — ausschließlich
  FrameBouncer-eigene Profile, niemals automatisch erkannte Prozesse. Restore validiert die Datei
  (Formatversion, EXE-Namen, FPS-Bereich, Duplikate), fragt nach, sichert die aktuelle Konfiguration
  automatisch als Safety-Backup und aktualisiert Persistenz + UI — ohne RTSS-Schreibvorgänge.
  `settings.json` wird atomar geschrieben (Temp-Datei + Replace). Details: `docs/architecture.md`.
- **GitHub-Release & sicherer Updater**: "⟳ Updates" prüft gegen GitHub Releases (offizielle API, nur
  HTTPS; Owner/Repository zentral in `UpdateConfiguration.cs` konfigurierbar, Prereleases ignoriert,
  kein Downgrade, 24-h-Cooldown für den Start-Check). Download → **SHA-256-Prüfung** gegen die
  `.sha256`-Metadaten-Datei des Releases → separater `FrameBouncer.Updater.exe` ersetzt die Dateien
  atomar (Backup + Rollback, Path-Traversal-Schutz) und startet die App neu. settings.json/Profile/
  Backups bleiben unangetastet; **keine digitale Signatur** (nur Hash, ehrlich dokumentiert).
  Release-Prozess: Tag `vX.Y.Z` → `.github/workflows/release.yml` (build → test → self-contained
  publish win-x64 → `FrameBouncer-vX.Y.Z-win-x64.zip` + `.sha256` → GitHub Release).
  Details: `docs/architecture.md`.
- **Diagnostics**: `diagnose.cs` (Repo-Root) ist ein eigenständiges Konsolen-Tool zum Prüfen
  des RTSS Shared Memory – nicht Teil der Solution, z.B. mit `csc diagnose.cs` bauen.
