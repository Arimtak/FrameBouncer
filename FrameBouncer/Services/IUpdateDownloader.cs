namespace FrameBouncer.Services;

/// <summary>
/// Read-only-Download der Update-Pakete (Spec Punkt 7/19): lädt das
/// versionierte Release-Asset (zip) UND dessen SHA-256-Metadaten-Datei
/// ausschließlich über HTTPS herunter. Implementierungen dürfen NIE werfen.
/// </summary>
public interface IUpdateDownloader
{
    /// <summary>
    /// Lädt zip + .sha256 in destinationDir. Liefert bei jedem Fehler
    /// <see cref="UpdateDownloadResult.Success"/> == false mit Benutzermeldung.
    /// </summary>
    Task<UpdateDownloadResult> DownloadAsync(
        GitHubAssetInfo zipAsset,
        GitHubAssetInfo shaAsset,
        string destinationDir,
        CancellationToken cancellationToken = default);
}