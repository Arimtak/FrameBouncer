namespace FrameBouncer.Models;

/// <summary>
/// Backup-Datei (Backup-Spec Punkt 4/9): versioniertes JSON-Format mit camelCase-
/// Schlüsseln (formatVersion/createdAt/profiles/...). Enthält ausschließlich die
/// von FrameBouncer verwalteten SavedProfiles – niemals erkannte Prozesse und
/// niemals fremde (nicht gelesene) RTSS-Konfiguration.
/// </summary>
public sealed class ProfileBackupFile
{
    /// <summary>Aktuelle Formatversion. Neue Profilfelder → neue Version (Punkt 9).</summary>
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; }

    /// <summary>Erstellungszeitpunkt des Backups (UTC) – Schlüssel "createdAt" wie im Spec-Beispiel.</summary>
    public DateTime CreatedAt { get; init; }

    /// <summary>Version der erstellenden Anwendung (nur Information, kein Pflichtfeld).</summary>
    public string AppVersion { get; init; } = string.Empty;

    public List<ProfileBackupEntry> Profiles { get; init; } = new();
}

/// <summary>Ein gespeichertes Profil im Backup. Feldnamen folgen dem Spec-Beispiel (enabled).</summary>
public sealed class ProfileBackupEntry
{
    public string ProcessName { get; init; } = string.Empty;

    public int TargetFps { get; init; }

    public bool Enabled { get; init; } = true;

    public DateTime CreatedUtc { get; init; }

    public DateTime UpdatedUtc { get; init; }
}

/// <summary>Ergebnis der Backup-Validierung mit benutzerfreundlicher Fehlermeldung (Punkt 8/15).</summary>
public sealed record BackupValidationResult
{
    public bool IsValid { get; init; }

    /// <summary>Deutsch, benutzerfreundlich, ohne interne Details (z.B. "ungültiges Format").</summary>
    public string Error { get; init; } = string.Empty;

    public ProfileBackupFile? Backup { get; init; }

    public static BackupValidationResult Ok(ProfileBackupFile backup) => new() { IsValid = true, Backup = backup };

    public static BackupValidationResult Fail(string error) => new() { IsValid = false, Error = error };
}
