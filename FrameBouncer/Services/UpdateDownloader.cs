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
        IProgress<double>? progress = null,
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
                    // Fortschritt nur, wenn der Server die Länge kennt (Content-Length);
                    // sonst ehrlich indeterminiert (-1) statt erfundener Prozente.
                    long? total = zipResponse.Content.Headers.ContentLength;
                    progress?.Report(0);
                    if (total is null or <= 0)
                    {
                        progress?.Report(-1);
                        await zipResponse.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await using var src = await zipResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                        var buffer = new byte[64 * 1024];
                        long read = 0;
                        int n;
                        while ((n = await src.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                        {
                            await fs.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                            read += n;
                            progress?.Report((double)read / total.Value);
                        }
                    }
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

            progress?.Report(1.0);
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