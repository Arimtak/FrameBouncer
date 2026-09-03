using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Quelle für den V-Sync-Zustand einer konkreten EXE bzw. Ebene
/// (NVIDIA-Treiber, AMD-Treiber, In-Game/Engine).
///
/// Regeln (Spec):
/// - Quellentreu: „NVIDIA V-Sync: Aktiv“ ist NICHT „Globaler V-Sync: Aktiv“.
/// - STRIKT nur lesend: nie Treiber-, Spiel-, RTSS-, Profil- oder TargetFps-Werte ändern.
/// - Implementierungen dürfen NIE werfen und NIE geratene Werte liefern.
/// - V-Sync hat KEINEN FPS-Wert – aktive V-Sync-Zustände werden ohne Limit geliefert.
/// </summary>
public interface IVSyncProvider
{
    /// <summary>Quellen-Kennung (NvidiaVSync / AmdVSync / InGameVSync) — nie global verallgemeinern.</summary>
    LimiterSource Source { get; }

    /// <summary>
    /// Liefert den V-Sync-Zustand für die EXE. Wenn der Zustand nicht verifizierbar
    /// ausgelesen werden kann → ehrlich <see cref="LimiterStatus.Unknown"/>;
    /// wenn die Quelle grundsätzlich nicht verfügbar ist (Treiber/API fehlt) →
    /// <see cref="LimiterStatus.Unavailable"/>. Niemals 0, niemals erfunden.
    /// </summary>
    LimiterState GetVSyncStateForProcess(string? processName);
}