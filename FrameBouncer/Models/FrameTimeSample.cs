using System;

namespace FrameBouncer.Models;

/// <summary>
/// Repräsentiert ein einzelnes Frametime-Mess-Sample.
/// Ausgelegt für hochfrequente RTSS-Statistikdaten.
/// </summary>
public record FrameTimeSample
{
    /// <summary>
    /// Zeitstempel der Frame-Erfassung
    /// </summary>
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Gemessene Frametime in Millisekunden (z.B. 16.67 ms)
    /// </summary>
    public double FrameTimeMs { get; init; }

    /// <summary>
    /// Berechnete oder von RTSS gemeldete FPS (1000.0 / FrameTimeMs)
    /// </summary>
    public double Fps { get; init; }

    /// <summary>
    /// Kennzeichnet auffällige Ausreißer / Spikes
    /// </summary>
    public bool IsSpike { get; init; }

    /// <summary>
    /// Ziel-Frametime zum Zeitpunkt der Messung
    /// </summary>
    public double TargetFrameTimeMs { get; init; }

    /// <summary>
    /// Herkunft des Werts: echte gemessene Frametime, aus FPS abgeleitet oder nicht verfügbar.
    /// </summary>
    public FrameTimeSource Source { get; init; } = FrameTimeSource.Unavailable;

    /// <summary>
    /// Prozessname (mit Endung) des gemessenen RTSS-Entrys – Grundlage für die
    /// Spielwechsel-Erkennung (Historien-Reset beim Prozesswechsel).
    /// </summary>
    public string ProcessName { get; init; } = string.Empty;
}
