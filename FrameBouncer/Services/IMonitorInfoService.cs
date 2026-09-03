using System;

namespace FrameBouncer.Services;

/// <summary>
/// Erkennung der von Windows aktuell verwendeten Monitor-/Displaymode-Konfiguration.
/// Lesend und rein diagnostisch; verändert niemals Display-Einstellungen, RTSS oder Profile.
///
/// Zielmonitor-Auswahl (mehrere Monitore):
/// 1. Monitor des Fensters des überwachten Prozesses (sofern zuordenbar)
/// 2. sonst der primäre Monitor
/// Niemals ein zufälliger Monitor.
/// </summary>
public interface IMonitorInfoService
{
    /// <summary>Alle aktuell angeschlossenen Monitore mit aktuellem Displaymode.</summary>
    IReadOnlyList<MonitorInfo> GetMonitors();

    /// <summary>Der primäre Windows-Monitor oder null, wenn nicht ermittelbar.</summary>
    MonitorInfo? GetPrimaryMonitor();

    /// <summary>
    /// Monitor, auf dem sich das Hauptfenster des Prozesses befindet (Fensterschwerpunkt),
    /// oder null, wenn der Prozess kein sichtbares Fenster hat / nicht gefunden wurde.
    /// </summary>
    MonitorInfo? GetMonitorForProcess(string processName);

    /// <summary>Monitor unter dem Mittelpunkt eines Fensterhandles.</summary>
    MonitorInfo? GetMonitorForWindow(IntPtr hWnd);

    /// <summary>
    /// Zielmonitor für die aktuelle Überwachung: Fenster des Prozesses, sonst primär.
    /// Liefert ein IsAvailable=false-Objekt statt zu raten, wenn Windows nichts
    /// Gültiges liefert.
    /// </summary>
    MonitorInfo GetTargetMonitor(string? processName);
}
