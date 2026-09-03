using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace FrameBouncer.Services;

/// <summary>
/// Offizielle GitHub-Releases-API (Spec Punkt 17): GET /repos/{owner}/{repo}/releases/latest.
/// Robust gegen 200/404/403/429/5xx, Netzwerkfehler und ungültiges JSON – nie werfend.
/// Nur HTTPS (Punkt 19), TLS-Zertifikatsprüfung bleibt aktiv (Standard-Handler).
/// Prereleases werden ignoriert (Punkt 4); Downgrades werden nie angeboten (Punkt 4).
/// </summary>
public class GitHubReleaseService : IGitHubReleaseService
{
    private readonly HttpClient _http;
    private readonly string _apiUrl;

    public GitHubReleaseService(HttpClient? http = null, string? owner = null, string? repository = null)
    {
        _http = http ?? CreateDefaultHttpClient();
        _apiUrl = $"https://api.github.com/repos/{owner ?? UpdateConfiguration.Owner}/{repository ?? UpdateConfiguration.Repository}/releases/latest";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _apiUrl);
            request.Headers.Add("Accept", "application/vnd.github+json");
            request.Headers.Add("User-Agent", $"FrameBouncer/{AppVersion.Current}");

            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Result(UpdateCheckStatus.NoRelease,
                    $"Updatequelle nicht gefunden ({UpdateConfiguration.Owner}/{UpdateConfiguration.Repository}).");

            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Result(UpdateCheckStatus.HttpError, "Update-Prüfung nicht möglich (Rate Limit).");

            if (!response.IsSuccessStatusCode)
                return Result(UpdateCheckStatus.HttpError, $"Update-Prüfung nicht möglich (HTTP {(int)response.StatusCode}).");

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            var release = ParseRelease(json);
            if (release is null)
                return Result(UpdateCheckStatus.InvalidData, "Update-Prüfung nicht möglich (ungültige Release-Daten).");

            return Evaluate(release, currentVersion);
        }
        catch (HttpRequestException)
        {
            return Result(UpdateCheckStatus.NoConnection, "Keine Internetverbindung.");
        }
        catch (TaskCanceledException)
        {
            return Result(UpdateCheckStatus.NoConnection, "Keine Internetverbindung (Timeout).");
        }
        catch (JsonException)
        {
            return Result(UpdateCheckStatus.InvalidData, "Update-Prüfung nicht möglich (ungültige Antwort).");
        }
        catch
        {
            // Kein Crash, keine Exception nach außen (Punkt 17/28)
            return Result(UpdateCheckStatus.NoConnection, "Update-Prüfung nicht möglich.");
        }
    }

    /// <summary>Prerelease ignorieren, Version vergleichen, Assets suchen (Punkt 4).</summary>
    private static UpdateCheckResult Evaluate(GitHubReleaseInfo release, string currentVersion)
    {
        if (release.IsPrerelease)
            return Result(UpdateCheckStatus.UpToDate, "Du verwendest die neueste stabile Version.");

        var latest = AppVersion.Parse(release.TagName);
        if (latest is null)
            return Result(UpdateCheckStatus.InvalidData, "Update-Prüfung nicht möglich (Version nicht lesbar).");

        var current = AppVersion.Parse(currentVersion) ?? new Version(0, 0, 0);

        // Gleich oder älter → kein Update, kein Downgrade (Punkt 4)
        if (latest <= current)
            return Result(UpdateCheckStatus.UpToDate, "Du verwendest die neueste Version.");

        var zipName = UpdateConfiguration.BuildAssetName(release.TagName);
        var shaName = UpdateConfiguration.BuildSha256AssetName(release.TagName);

        var zipAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, zipName, StringComparison.OrdinalIgnoreCase));
        var shaAsset = release.Assets.FirstOrDefault(a =>
            string.Equals(a.Name, shaName, StringComparison.OrdinalIgnoreCase));

        if (zipAsset is null || shaAsset is null)
            return new UpdateCheckResult
            {
                Status = UpdateCheckStatus.AssetMissing,
                LatestVersion = release.TagName,
                Release = release,
                Message = "Neue Version verfügbar, aber das Update-Paket fehlt."
            };

        return new UpdateCheckResult
        {
            Status = UpdateCheckStatus.UpdateAvailable,
            LatestVersion = release.TagName,
            Release = release,
            ZipAsset = zipAsset,
            ShaAsset = shaAsset,
            Message = $"Neue Version verfügbar: {release.TagName}"
        };
    }

    private static GitHubReleaseInfo? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(tag)) return null;

        var prerelease = root.TryGetProperty("prerelease", out var pr) &&
                         pr.ValueKind == JsonValueKind.True;

        var assets = new List<GitHubAssetInfo>();
        if (root.TryGetProperty("assets", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in arr.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                // Nur HTTPS-Quellen zulassen (Punkt 19)
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(url) &&
                    url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    assets.Add(new GitHubAssetInfo { Name = name, BrowserDownloadUrl = url });
                }
            }
        }

        return new GitHubReleaseInfo { TagName = tag, IsPrerelease = prerelease, Assets = assets };
    }

    private static UpdateCheckResult Result(UpdateCheckStatus status, string message) =>
        new() { Status = status, Message = message };

    private static HttpClient CreateDefaultHttpClient()
    {
        // Standard-Handler: TLS-Zertifikatsprüfung bleibt AKTIV (Punkt 19).
        return new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
    }
}