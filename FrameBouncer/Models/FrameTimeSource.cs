namespace FrameBouncer.Models;

/// <summary>
/// Herkunft eines Frametime/FPS-Messwerts. Wichtig für ehrliche Anzeige:
/// Nur "Measured" ist eine echte, von RTSS gelieferte Frame-Time.
/// </summary>
public enum FrameTimeSource
{
    /// <summary>Keine gültigen Daten (FT280=0 und kein Frames/Δt-Fallback).</summary>
    Unavailable = 0,

    /// <summary>Echte Frametime: RTSS dwFrameTime (Offset 280) in Mikrosekunden.</summary>
    Measured = 1,

    /// <summary>Abgeleitet: Frametime = 1000/FPS aus RTSS Frames/(Time1−Time0) (kein dwFrameTime).</summary>
    Derived = 2
}
