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
    /// <param name="progress">
    /// Optionaler Fortschritt des zip-Downloads als Anteil 0..1.
    /// -1 bedeutet "Länge unbekannt" (indeterminierter Fortschritt).
    /// </param>
    Task<UpdateDownloadResult> DownloadAsync(
        GitHubAssetInfo zipAsset,
        GitHubAssetInfo shaAsset,
        string destinationDir,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}