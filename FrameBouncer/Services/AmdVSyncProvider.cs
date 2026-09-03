using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// AMD-Treiber-V-Sync (Radeon-Software-V-Sync-Einstellung).
///
/// Verifizierbare Quellen (Spec Punkt 3): ADLX (AMD Display Library) dokumentiert
/// kein Lesen des V-Sync-Status pro Anwendung; Registry-Werte wären Registry-
/// Heuristik und sind laut Spec verboten („Keine Registry-Heuristik als sichere
/// globale Wahrheit“).
///
/// Konsequenz: Der Zustand wird ehrlich als <see cref="LimiterStatus.Unknown"/>
/// geliefert. Ein AMD-System bedeutet NICHT, dass AMD V-Sync bekannt ist.
/// </summary>
public class AmdVSyncProvider : IVSyncProvider
{
    public LimiterSource Source => LimiterSource.AmdVSync;

    public LimiterState GetVSyncStateForProcess(string? processName)
        => LimiterState.Unknown(LimiterSource.AmdVSync);
}