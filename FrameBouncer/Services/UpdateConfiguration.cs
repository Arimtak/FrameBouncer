namespace FrameBouncer.Services;

/// <summary>
/// Zentrale, konfigurierbare Updatequelle (Update-Spec Punkt 2):
/// Owner/Repository/Channel an GENAU EINER Stelle – keine hartcodierten URLs
/// im Code. Es wird kein eigener Server betrieben; GitHub Releases ist die
/// offizielle Updatequelle.
///
/// ⚠️ VOR DEM ERSTEN RELEASE hier die ECHTEN GitHub-Daten eintragen:
/// Owner und Repository des tatsächlichen Repositories.
/// </summary>
public static class UpdateConfiguration
{
    /// <summary>GitHub-Organisation/Benutzer des Repositories.</summary>
    public const string Owner = "Arimtak";

    /// <summary>Name des Repositories.</summary>
    public const string Repository = "FrameBouncer";

    /// <summary>
    /// Update-Kanal: "stable" ignoriert Prereleases (Spec Punkt 4).
    /// </summary>
    public const string Channel = "stable";

    /// <summary>Offizielle GitHub-Releases-API (nur HTTPS, Spec Punkt 19).</summary>
    public static string LatestReleaseUrl =>
        $"https://api.github.com/repos/{Owner}/{Repository}/releases/latest";

    /// <summary>Name des Update-Paket-Assets, z. B. "FrameBouncer-v1.1.0-win-x64.zip".</summary>
    public static string BuildAssetName(string tagName) => $"FrameBouncer-{tagName}-win-x64.zip";

    /// <summary>Name der SHA-256-Metadaten-Datei zum Paket.</summary>
    public static string BuildSha256AssetName(string tagName) => BuildAssetName(tagName) + ".sha256";
}