namespace FrameBouncer.Models;

public record AppSettings
{
    public int TargetFps { get; init; } = 60;
    public string? SelectedProcess { get; init; }
    public bool IsTopmost { get; init; }
    public bool IsAutostartEnabled { get; init; }
    public List<string> SavedProcesses { get; init; } = new();
    public List<GameProfile> SavedProfiles { get; init; } = new();

    /// <summary>
    /// RTSS beim App-Start mitstarten, falls es nicht läuft (Standard: aus).
    /// </summary>
    public bool StartRtssWithApp { get; init; }

    /// <summary>
    /// MSI Afterburner beim App-Start mitstarten, falls es nicht läuft (Standard: aus).
    /// </summary>
    public bool StartAfterburnerWithApp { get; init; }

    /// <summary>
    /// Zeitpunkt des letzten automatischen Update-Checks (UTC) – Cooldown für den
    /// Start-Check (max. 1×/24 h, Spec Punkt 5). Manuelle Checks umgehen das.
    /// </summary>
    public DateTime? LastUpdateCheckUtc { get; init; }

    /// <summary>
    /// GitHub-Owner (Organisation/Benutzer) der Updatequelle. null → Platzhalter
    /// aus <see cref="FrameBouncer.Services.UpdateConfiguration"/>. Zentral
    /// konfigurierbar, damit die App ohne Neubau auf das echte Repo zeigt.
    /// </summary>
    public string? UpdateOwner { get; init; }

    /// <summary>GitHub-Repository-Name der Updatequelle. null → Platzhalter-Konfiguration.</summary>
    public string? UpdateRepository { get; init; }
}
