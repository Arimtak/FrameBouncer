using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// AMD-Treiber-FPS-Limit (Frame Rate Target Control / Radeon-Software-Limit).
///
/// Verifizierbare Quellen (Spec Punkt 4): ADLX (AMD Display Library) dokumentiert
/// KEIN Lesen des FRTC-Werts pro Anwendung; Registry-Werte unter dem UMD-Schlüssel
/// wären Registry-Heuristik und sind laut Spec verboten („Keine Registry-Heuristik
/// als tatsächlichen FPS-Cap ausgeben“).
///
/// Konsequenz: Das Limit wird ehrlich als <see cref="LimiterStatus.Unknown"/>
/// geliefert. Ein AMD-System bedeutet NICHT, dass ein AMD-FPS-Limit bekannt ist.
/// </summary>
public class AmdDriverLimitProvider : IDriverLimitProvider
{
    public LimiterSource Source => LimiterSource.Amd;

    public LimiterState GetLimitForProcess(string? processName)
        => LimiterState.Unknown(LimiterSource.Amd);
}