using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Quelle für das GPU-Treiber-FPS-Limit einer konkreten EXE
/// (NVIDIA Frame Rate Limiter / „Max Frame Rate“, AMD FRTC).
///
/// Regeln (Spec):
/// - Per-Game: Die aktuell überwachte EXE wird bei jedem Aufruf übergeben.
/// - STRIKT nur lesend: nie Treiber-, RTSS-, Profil- oder Spiel-Einstellungen ändern.
/// - Implementierungen dürfen NIE werfen und NIE geratene Werte liefern.
/// - Ein GPU-Hersteller-Nachweis ist KEIN Limit-Nachweis: NVIDIA/AMD erkannt
///   bedeutet nicht, dass ein FPS-Limit bekannt ist.
/// </summary>
public interface IDriverLimitProvider
{
    /// <summary>Quellen-Kennung (Nvidia oder Amd) — nur die passende wird abgefragt.</summary>
    LimiterSource Source { get; }

    /// <summary>
    /// Liefert den Treiber-Limit-Zustand für die EXE. Wenn der Wert nicht
    /// verifizierbar ausgelesen werden kann → ehrlich
    /// <see cref="LimiterStatus.Unknown"/> (niemals 0, niemals erfunden).
    /// </summary>
    LimiterState GetLimitForProcess(string? processName);
}