namespace FrameBouncer.Services;

/// <summary>Ergebnis-Zustände der Update-Prüfung (Spec Punkt 17/18).</summary>
public enum UpdateCheckStatus
{
    /// <summary>Aktuelle Version ist die neueste (auch: kein Downgrade, Prerelease ignoriert).</summary>
    UpToDate = 0,

    /// <summary>Neuere stabile Version mit vollständigem Paket gefunden.</summary>
    UpdateAvailable = 1,

    /// <summary>GitHub nicht erreichbar / keine Verbindung (Offline).</summary>
    NoConnection = 2,

    /// <summary>HTTP-Fehler (z. B. 5xx, Rate Limit).</summary>
    HttpError = 3,

    /// <summary>Antwort nicht lesbar / ungültiges JSON / ungültige Version.</summary>
    InvalidData = 4,

    /// <summary>Kein Release gefunden (Repository/Release nicht vorhanden).</summary>
    NoRelease = 5,

    /// <summary>Release vorhanden, aber das Update-Paket-Asset fehlt.</summary>
    AssetMissing = 6
}

/// <summary>Ein GitHub-Release-Asset (nur Name + HTTPS-Download-URL).</summary>
public sealed record GitHubAssetInfo
{
    public string Name { get; init; } = string.Empty;
    public string BrowserDownloadUrl { get; init; } = string.Empty;
}

/// <summary>Relevante Release-Informationen aus der GitHub-API.</summary>
public sealed record GitHubReleaseInfo
{
    public string TagName { get; init; } = string.Empty;
    public bool IsPrerelease { get; init; }
    public IReadOnlyList<GitHubAssetInfo> Assets { get; init; } = Array.Empty<GitHubAssetInfo>();
}

/// <summary>Ergebnis der Update-Prüfung (Spec Punkt 4/17).</summary>
public sealed record UpdateCheckResult
{
    public UpdateCheckStatus Status { get; init; } = UpdateCheckStatus.NoConnection;
    public string? LatestVersion { get; init; }
    public GitHubReleaseInfo? Release { get; init; }
    public GitHubAssetInfo? ZipAsset { get; init; }
    public GitHubAssetInfo? ShaAsset { get; init; }

    /// <summary>Benutzerfreundliche Meldung (Spec Punkt 28 – keine Stacktraces).</summary>
    public string Message { get; init; } = string.Empty;
}

/// <summary>Ergebnis des Paket-Downloads (nur HTTPS, Spec Punkt 19).</summary>
public sealed record UpdateDownloadResult
{
    public bool Success { get; init; }
    public string? ZipPath { get; init; }
    public string? Sha256Path { get; init; }
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// Ergebnis der SHA-256-Verifikation. Code-Signatur ist derzeit NICHT vorhanden
/// (Spec Punkt 9): `SignatureValidated` ist daher immer false und wird ehrlich
/// in der Meldung unterschieden („Hash validiert“ ≠ „Signatur validiert“).
/// </summary>
public sealed record UpdateVerificationResult
{
    public bool IsValid { get; init; }
    public string? ComputedHash { get; init; }
    public string? ExpectedHash { get; init; }
    public bool SignatureValidated { get; init; }
    public string Error { get; init; } = string.Empty;
}

/// <summary>Ergebnis des Updater-Starts (App-seitig, Spec Punkt 10).</summary>
public sealed record UpdateLaunchResult
{
    public bool Success { get; init; }
    public string Error { get; init; } = string.Empty;
}