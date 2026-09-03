namespace FrameBouncer.Models;

/// <summary>
/// Bekannte FPS-Limiter-Quellen. Nur Quellen, die die App zuverlässig
/// ansprechen kann, führen zu einem Known-Zustand – alles andere bleibt
/// ehrlich Unbekannt (keine Fake-Erkennung).
/// </summary>
public enum LimiterSource
{
    /// <summary>RTSS (eigene Integration: Shared Memory + ProfileTemplates).</summary>
    Rtss = 0,

    /// <summary>Im Spiel eingestellter Limiter (nur aus sicheren Quellen erkennbar).</summary>
    InGame = 1,

    /// <summary>NVIDIA Frame Rate Limiter / Max Frame Rate (DRS-Setting).</summary>
    Nvidia = 2,

    /// <summary>AMD Frame Rate Target Control / vergleichbar.</summary>
    Amd = 3,

    /// <summary>V-Sync, generisch (kein FPS-Cap im engeren Sinne, aber frame-ratenwirksam).</summary>
    VSync = 4,

    /// <summary>VRR (G-Sync/FreeSync) – wird bewusst NICHT als Limiter-Konflikt gewertet.</summary>
    Vrr = 5,

    /// <summary>NVIDIA-Treiber-V-Sync (DRS-Setting) – eigene Quelle, nie global verallgemeinern.</summary>
    NvidiaVSync = 6,

    /// <summary>AMD-Treiber-V-Sync – eigene Quelle, nie global verallgemeinern.</summary>
    AmdVSync = 7,

    /// <summary>Im Spiel/der Engine eingestelltes V-Sync – eigene Quelle.</summary>
    InGameVSync = 8
}

/// <summary>Verlässlichkeits-Zustand einer Limiter-Quelle (Spec Punkt 2).</summary>
public enum LimiterStatus
{
    /// <summary>Nicht ermittelbar – NIEMALS als "Aus" interpretieren.</summary>
    Unknown = 0,

    /// <summary>Zuverlässig ermittelt: kein Limit aktiv.</summary>
    Off = 1,

    /// <summary>Zuverlässig ermittelt: Limit aktiv mit KnownLimitFps.</summary>
    On = 2,

    /// <summary>Quelle grundsätzlich nicht verfügbar (z. B. Treiber/API fehlt) – nie als "Aus" deuten.</summary>
    Unavailable = 3
}

/// <summary>
/// Zustand einer einzelnen Limiter-Quelle. Unveränderlich (record).
/// </summary>
public sealed record LimiterState
{
    public LimiterSource Source { get; init; }

    public LimiterStatus Status { get; init; } = LimiterStatus.Unknown;

    /// <summary>Bekanntes FPS-Limit (nur gültig, wenn Status == On).</summary>
    public int? LimitFps { get; init; }

    /// <summary>true nur, wenn der Zustand zuverlässig bestimmt werden konnte (Aus oder Aktiv).</summary>
    public bool IsKnown => Status is LimiterStatus.Off or LimiterStatus.On;

    /// <summary>true nur, wenn zuverlässig ein aktives Limit festgestellt wurde.</summary>
    public bool IsActive => Status == LimiterStatus.On;

    public static LimiterState Unknown(LimiterSource source) => new() { Source = source };

    public static LimiterState Off(LimiterSource source) => new() { Source = source, Status = LimiterStatus.Off };

    public static LimiterState On(LimiterSource source, int limitFps) =>
        new() { Source = source, Status = LimiterStatus.On, LimitFps = limitFps };

    /// <summary>Aktiver Zustand ohne FPS-Wert (z. B. „V-Sync aktiv“).</summary>
    public static LimiterState Active(LimiterSource source) =>
        new() { Source = source, Status = LimiterStatus.On };

    /// <summary>Quelle grundsätzlich nicht verfügbar (z. B. Treiber/API fehlt).</summary>
    public static LimiterState Unavailable(LimiterSource source) =>
        new() { Source = source, Status = LimiterStatus.Unavailable };
}

/// <summary>
/// Ergebnis der Konfliktanalyse (pur, testbar – Spec Punkt 12).
/// </summary>
public sealed record LimiterConflictResult
{
    /// <summary>true nur, wenn ≥ 2 Quellen ZUVERLÄSSIG als aktiv bekannt sind.</summary>
    public bool HasConflict { get; init; }

    /// <summary>Alle bewerteten Quellen-Zustände.</summary>
    public IReadOnlyList<LimiterState> DetectedLimiters { get; init; } = Array.Empty<LimiterState>();

    /// <summary>
    /// Diagnose-Hinweis: niedrigstes sicher bekanntes aktives Limit (nur wenn ≥ 2 aktiv).
    /// Wird NIEMALS automatisch angewendet (Spec Punkt 8).
    /// </summary>
    public int? EffectiveLimitHint { get; init; }

    /// <summary>Verständliche, vorsichtig formulierte Meldung (Spec Punkt 7).</summary>
    public string Message { get; init; } = string.Empty;
}
