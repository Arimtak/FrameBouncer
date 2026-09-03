# FrameBouncer

Ein kleines Windows-Programm, um die **FPS in Spielen zu begrenzen** (z. B. 60, 120 oder 144),
damit der PC ruhiger läuft, kühler bleibt und weniger Strom verbraucht. Die Einstellungen werden
pro Spiel gespeichert und beim nächsten Start automatisch wieder angewendet.

**Frei nutzbar für alle** – MIT-Lizenz (siehe [LICENSE](LICENSE)).

## Screenshot

![FrameBouncer-Oberfläche](docs/screenshot.png)

## Installation

1. Lade die neueste Version unter **GitHub Releases** herunter:
   `FrameBouncer-vX.Y.Z-win-x64.zip`
2. Entpacke das ZIP in einen beliebigen Ordner (z. B. `D:\Tools\FrameBouncer`).
3. **RTSS** (RivaTuner Statistics Server, gratis auf guru3d.com) installieren und starten –
   ohne RTSS kann FrameBouncer kein FPS-Limit setzen.
4. `FrameBouncer.exe` starten – fertig, keine Installation nötig.

> Beim allerersten „Apply" kann einmal ein Windows-Sicherheitsfenster (UAC) erscheinen.
> Danach fragt FrameBouncer nie wieder.

## Bedienung

1. **Spiel auswählen:** Wähle dein laufendes Spiel aus der Liste oben (oder klicke auf
   **Fenster auswählen** und dann ins Spiel).
2. **FPS einstellen:** Wert eintippen oder Schnellwahl nutzen (30 / 60 / 120 / 144).
3. **Apply drücken** – das Spiel ist ab sofort begrenzt.

Das Profil wird automatisch gespeichert: Beim nächsten Start des Spiels gilt das Limit
wieder von selbst. Schließt du FrameBouncer, werden alle Limits wieder aufgehoben.

| Symbol / Button | Bedeutung |
|---|---|
| 📌 **Pin** | Fenster immer im Vordergrund |
| **Autostart** | Startet automatisch mit Windows |
| 🔔 **Tray** | Läuft nach ✕ im Infobereich weiter |
| ⭳ **Backup** / ⭱ **Restore** | Profile sichern / wiederherstellen |
| ⟳ **Updates** | Nach neuer Version suchen |

Unten zeigt das Programm live FPS, Frametime, 1%-/0,1%-Low und (wenn MSI Afterburner läuft)
GPU-/CPU-Temperatur.

## Deinstallieren

Einfach den Ordner löschen. In `%APPDATA%\FrameBouncer` liegen nur die eigenen Einstellungen –
auch die kann man löschen.

## Lizenz

[MIT](LICENSE) – du darfst das Programm frei nutzen, kopieren, verändern und weitergeben.
Entwickler-Details: [docs/architecture.md](docs/architecture.md).
