using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Backup/Restore der von FrameBouncer verwalteten SavedProfiles (Punkt 6/18).
/// Trennung: Serialisierung (DTOs), Validierung (BackupValidator), Dateispeicherung
/// (AtomicFile + BackupDirectory) und Restore. Rein diagnostisch gegenüber RTSS –
/// Restore schreibt niemals RTSS-Limits (Punkt 12/13) und berührt nur FrameBouncer-
/// eigene Profildaten (Punkt 3/11).
/// </summary>
public interface IProfileBackupService
{
    /// <summary>Aktuelle Backup-Dateien (neueste zuerst, nur *.json im Backup-Verzeichnis).</summary>
    IReadOnlyList<string> ListBackups();

    /// <summary>
    /// Erzeugt eine Backup-Datei aus den gegebenen Profilen und gibt Pfad + Dateinamen zurück.
    /// Wird ausschließlich durch die explizite Benutzeraktion aufgerufen.
    /// </summary>
    BackupCreatedResult CreateBackup(IReadOnlyList<GameProfile> profiles, DateTime? nowUtc = null, string? explicitPath = null);

    /// <summary>Liest und validiert eine Backup-Datei (kein blindes Laden, Punkt 8).</summary>
    BackupValidationResult ReadAndValidate(string path);

    /// <summary>
    /// Wendet ein bereits validiertes Backup als neue SavedProfiles-Liste an. Vor dem
    /// Überschreiben wird automatisch ein Safety-Backup der aktuellen Konfiguration
    /// geschrieben (Punkt 7: aktuelle Konfiguration geht bei Fehlschlag nicht verloren).
    /// Wirft bei Schreibfehlern – der Aufrufer behandelt die Anzeige.
    /// </summary>
    IReadOnlyList<GameProfile> RestoreBackup(ProfileBackupFile backup, IReadOnlyList<GameProfile> currentProfiles, DateTime? nowUtc = null);

    /// <summary>Standardverzeichnis für Backup-Dateien.</summary>
    string BackupDirectory { get; }
}

public sealed record BackupCreatedResult(string FilePath, string FileName, int ProfileCount);
