using System.IO;
using System.Net.Http;

namespace FrameBouncer.Services;

/// <summary>
/// Lädt das Update-Paket (zip) und die SHA-256-Metadaten-Datei herunter
/// (Spec Punkt 7). Ausschließlich HTTPS (Punkt 19); Nicht-HTTPS-URLs werden
/// abgelehnt. Nie werfend – jeder Fehler wird als Ergebnis gemeldet.
/// </summary>
public class UpdateDownloader : IUpdateDownloader
{
    private readonly HttpClient _http;

    public UpdateDownloader(HttpClient? http = null)
    {
        // Standard-Handler: TLS-Zertifikatsprüfung bleibt AKTIV (Punkt 19).
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    public async Task<UpdateDownloadResult> DownloadAsync(
        GitHubAssetInfo zipAsset,
        GitHubAssetInfo shaAsset,
        string destinationDir,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!zipAsset.BrowserDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                !shaAsset.BrowserDownloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return new UpdateDownloadResult { Error = Localization.T("Update.HttpsOnly") };
            }

            Directory.CreateDirectory(destinationDir);
            var zipPath = Path.Combine(destinationDir, zipAsset.Name);
            var shaPath = Path.Combine(destinationDir, shaAsset.Name);

            using (var zipResponse = await _http.GetAsync(zipAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false))
            {
                if (!zipResponse.IsSuccessStatusCode)
                    return new UpdateDownloadResult { Error = Localization.TFmt("Update.DownloadFailedHttpFmt", (int)zipResponse.StatusCode) };
                await using (var fs = File.Create(zipPath))
                {
                    await zipResponse.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }
            }

            using (var shaResponse = await _http.GetAsync(shaAsset.BrowserDownloadUrl, cancellationToken).ConfigureAwait(false))
            {
                if (!shaResponse.IsSuccessStatusCode)
                    return new UpdateDownloadResult { Error = Localization.T("Update.ShaDownloadFailed") };
                await using (var fs = File.Create(shaPath))
                {
                    await shaResponse.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                }
            }

            return new UpdateDownloadResult { Success = true, ZipPath = zipPath, Sha256Path = shaPath };
        }
        catch (HttpRequestException)
        {
            return new UpdateDownloadResult { Error = Localization.T("Update.NoConnection") };
        }
        catch (TaskCanceledException)
        {
            return new UpdateDownloadResult { Error = Localization.T("Update.Timeout") };
        }
        catch (Exception ex)
        {
            return new UpdateDownloadResult { Error = Localization.TFmt("Update.DownloadFailedGenericFmt", ex.Message) };
        }
    }
}