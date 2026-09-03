using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Detector für ein spielinternes FPS-Limit einer konkreten
/// Engine/Spielgruppe (Spec Punkte 3/5/6).
///
/// Regeln:
/// - Nur VERIFIZIERBARE, dokumentierte Konfigurationsquellen lesen.
/// - STRIKT nur lesend: nie Spieldateien verändern oder erzeugen.
/// - Implementierungen dürfen NIE werfen und NIE geratene Werte liefern.
/// - `CanHandle` identifiziert die Engine/Spielfamilie anhand stabiler
///   Signaturen (keine riesige EXE-Blacklist, Punkt 7).
/// - `Detect` liefert nur sichere Werte; Unsicherheit → Unknown/Unavailable.
/// </summary>
public interface IInGameLimiterDetector
{
    /// <summary>true, wenn dieser Detector die Engine/das Spiel des Kontexts versteht.</summary>
    bool CanHandle(GameContext context);

    /// <summary>
    /// Liefert den In-Game-Limiter-Zustand. Zustände: On(LimitFps&gt;0),
    /// Off (sicher deaktiviert), Unknown (nicht sicher ermittelbar),
    /// Unavailable (Datenquelle nicht verfügbar). Niemals 0 als Limit.
    /// </summary>
    LimiterState Detect(GameContext context);
}