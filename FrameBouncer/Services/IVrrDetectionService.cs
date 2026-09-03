namespace FrameBouncer.Services;

/// <summary>
/// VRR-Erkennung für den Zielmonitor. Rein diagnostisch und lesend:
/// verändert niemals RTSS, gespeicherte Profile, Display- oder
/// Treibereinstellungen.
///
/// Ehrlichkeits-Regeln (Spec Punkte 2/3/13):
/// - Unterstützung wird aus den VESA-EDID Range Limits abgeleitet (verifizierbare
///   Quelle, konservativer Heuristik-Check).
/// - Aktiver Status: Windows bietet KEINE öffentlich dokumentierte API für den
///   aktiven VRR-Zustand → ehrlich <see cref="VrrState.Unknown"/>.
/// - Technologie (G-SYNC/FreeSync/Adaptive Sync): aus EDID/Windows nicht
///   zuverlässig bestimmbar → ehrlich <see cref="VrrTechnology.Unknown"/>.
/// - Aus GPU-Hersteller oder Monitorname wird NIE geschlossen.
/// </summary>
public interface IVrrDetectionService
{
    /// <summary>Erweitert den Monitor um VRR-Informationen (unveränderte Kopie).</summary>
    MonitorInfo Detect(MonitorInfo monitor);
}