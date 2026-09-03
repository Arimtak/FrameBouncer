namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Quelle für GitHub-Releases (Spec Punkt 17/20). Verwendet die
/// offizielle GitHub-Releases-API über HTTPS. Implementierungen dürfen NIE
/// werfen – Netzwerkfehler, Rate Limits, ungültiges JSON etc. werden als
/// <see cref="UpdateCheckStatus"/>-Ergebnis gemeldet.
/// </summary>
public interface IGitHubReleaseService
{
    /// <summary>
    /// Prüft gegen die aktuelle Version. Liefert UpdateAvailable nur bei einer
    /// neueren STABILEN Version mit vollständigem Paket (zip + sha256); kein
    /// Downgrade, Prereleases werden ignoriert (Spec Punkt 4).
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default);
}