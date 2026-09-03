using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Diagnostische Erkennung gleichzeitiger FPS-Limiter (Spec Punkt 12).
/// Ausschließlich lesend: Niemals RTSS, Treiber, Windows oder Spiel-
/// konfigurationen verändern (Punkt 15).
/// </summary>
public interface ILimiterConflictService
{
    /// <summary>
    /// Sammelt die Limiter-Zustände und bewertet sie. Implementierungen sollten
    /// cachen/drosseln (Punkt 14) und niemals werfen.
    /// </summary>
    LimiterConflictResult Detect(TimeSpan? minDetectInterval = null);
}
