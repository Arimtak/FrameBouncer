using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Reine Rechenlogik: wandelt die Rohwerte eines RTSS-App-Entrys in FPS/Frametime um.
/// Öffentlich und testbar, damit die µs→ms-Konvention und die FT280=0-Behandlung
/// unit-testbar sind, ohne Shared Memory zu benötigen.
/// </summary>
public static class RtssFrameDataParser
{
    /// <summary>
    /// Konvention (dokumentiert in docs/architecture.md):
    /// RTSS dwFrameTime (Offset 280) ist die Frametime in MIKROSEKUNDEN.
    /// FPS = 1.000.000 / dwFrameTime, Frametime_ms = dwFrameTime / 1000.
    /// dwFrameTime == 0 bedeutet "kein gültiger Frame-Time-Wert" →
    /// Fallback Frames/(Time1−Time0) in Millisekunden → Frametime_ms = 1000/FPS.
    /// </summary>
    public static (double Fps, double FrameTimeMs, FrameTimeSource Source) Parse(
        uint frameTimeUs, uint time0Ms, uint time1Ms, uint frames)
    {
        // 1) Echte Frametime (µs) bevorzugt. 0 heißt: von RTSS nicht geliefert —
        //    niemals als echte 0 ms behandeln.
        if (frameTimeUs > 0)
        {
            double frameTimeMs = frameTimeUs / 1000.0;
            return (1000000.0 / frameTimeUs, frameTimeMs, FrameTimeSource.Measured);
        }

        // 2) Fallback: FPS aus Frames/Δt, Frametime abgeleitet (1000/FPS).
        if (time1Ms > time0Ms && frames > 0)
        {
            double fps = 1000.0 * frames / (time1Ms - time0Ms);
            return (fps, 1000.0 / fps, FrameTimeSource.Derived);
        }

        // 3) Kein brauchbarer Wert → ehrlich "nicht verfügbar" (0/0).
        return (0, 0, FrameTimeSource.Unavailable);
    }
}
