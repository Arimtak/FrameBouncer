using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Reine Konfliktlogik (kein I/O, deterministisch – Spec Punkt 12):
/// DetectedLimiters → ConflictAnalyzer → LimiterConflictResult.
///
/// Regeln:
/// - Nur ZUVERLÄSSIG aktive FPS-Limits (Status=On) zählen für einen Konflikt.
/// - Unbekannte/Unavailable Quellen werden nie als aktiv interpretiert (Punkt 2/6).
/// - V-Sync ist auf KEINER Ebene ein FPS-Limiter (Punkt 7): weder allein noch
///   zusammen mit RTSS/anderen erzeugt V-Sync einen Konflikt – dafür braucht es
///   mindestens zwei sicher aktive FPS-Limits.
/// - VRR + V-Sync ist eine normale Konfiguration (9), VRR wird gar nicht erst
///   als Limiter bewertet.
/// - Bei ≥ 2 aktiven FPS-Limits: vorsichtiger Hinweis + niedrigstes bekanntes
///   Limit als Diagnosewert (7/8).
/// </summary>
public static class ConflictAnalyzer
{
    public static LimiterConflictResult Analyze(IReadOnlyList<LimiterState> limiters)
    {
        var activeFpsLimiters = new List<LimiterState>();
        var activeVSync = new List<LimiterState>();
        foreach (var l in limiters)
        {
            // VRR ist kein Limiter (Punkt 9) – bewusst ignoriert.
            if (l.Source == LimiterSource.Vrr) continue;

            // Unbekannt/Unavailable wird nie als aktiv behandelt (Punkt 2/6).
            if (!l.IsActive) continue;

            // V-Sync (jede Ebene) ist KEIN FPS-Limiter (Punkt 7) – nur für die
            // Statusmeldung gesammelt, nie für einen Konflikt.
            if (IsVSyncSource(l.Source))
            {
                activeVSync.Add(l);
                continue;
            }

            activeFpsLimiters.Add(l);
        }

        // V-Sync allein (auch zusammen mit nicht-aktiven/unbekannten Quellen)
        // ist kein FPS-Limiter-Konflikt (Punkt 7): Es braucht mindestens zwei
        // sicher aktive FPS-Limits.
        if (activeFpsLimiters.Count == 0)
        {
            return NoConflict(limiters,
                activeVSync.Count > 0
                    ? Localization.T("Conflict.VSyncOnlyMessage")
                    : Localization.T("Conflict.NoActiveLimiters"));
        }

        if (activeFpsLimiters.Count == 1)
        {
            return NoConflict(limiters,
                Localization.TFmt("Conflict.ActiveLimitFmt", Describe(activeFpsLimiters[0])));
        }

        // ≥ 2 aktive FPS-Limits → Konflikt-Hinweis (vorsichtig formuliert, Punkt 7)
        var knownActiveLimits = activeFpsLimiters.Where(a => a.LimitFps is int).Select(a => a.LimitFps!.Value).ToList();
        int? lowest = knownActiveLimits.Count > 0 ? knownActiveLimits.Min() : null;

        string detail = string.Join(", ", activeFpsLimiters.Select(Describe));
        string message = Localization.TFmt("Conflict.MultipleLimitersFmt", detail);

        return new LimiterConflictResult
        {
            HasConflict = true,
            DetectedLimiters = limiters,
            EffectiveLimitHint = lowest,
            Message = message
        };
    }

    /// <summary>true für alle V-Sync-Quellen (nie FPS-Limiter).</summary>
    public static bool IsVSyncSource(LimiterSource source) =>
        source is LimiterSource.VSync or LimiterSource.NvidiaVSync or LimiterSource.AmdVSync or LimiterSource.InGameVSync;

    /// <summary>true nur für Quellen, die ein FPS-Limit setzen können.</summary>
    public static bool IsFpsLimiterSource(LimiterSource source) =>
        source is LimiterSource.Rtss or LimiterSource.InGame or LimiterSource.Nvidia or LimiterSource.Amd;

    private static LimiterConflictResult NoConflict(IReadOnlyList<LimiterState> limiters, string message) =>
        new()
        {
            HasConflict = false,
            DetectedLimiters = limiters,
            EffectiveLimitHint = null,
            Message = message
        };

    private static string Describe(LimiterState l) =>
        l.LimitFps is int fps ? $"{SourceName(l.Source)}: {fps} FPS" : $"{SourceName(l.Source)} aktiv";

    public static string SourceName(LimiterSource source) => source switch
    {
        LimiterSource.Rtss => "RTSS",
        LimiterSource.InGame => "In-Game",
        LimiterSource.Nvidia => "NVIDIA",
        LimiterSource.Amd => "AMD",
        LimiterSource.VSync => "V-Sync",
        LimiterSource.Vrr => "VRR",
        LimiterSource.NvidiaVSync => "NVIDIA V-Sync",
        LimiterSource.AmdVSync => "AMD V-Sync",
        LimiterSource.InGameVSync => "In-Game V-Sync",
        _ => source.ToString()
    };
}
