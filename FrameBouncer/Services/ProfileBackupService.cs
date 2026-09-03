using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Dateisystem-Layer des Backup-Dienstes (Punkt 18): Serialisierung ist in den DTOs,
/// Validierung im BackupValidator, dieser Service orchestriert Dateispeicherung und
/// Restore. Alle Schreibvorgänge atomar (Punkt 10), Restore mit vorangestelltem
/// Safety-Backup (Punkt 7). Enthält keinerlei RTSS-/Treiber-Logik – ein Backup
/// behauptet niemals, fremde RTSS-Konfiguration zu enthalten, die nicht gelesen wurde
/// (Punkt 3): gesichert werden ausschließlich FrameBouncer-SavedProfiles.
/// </summary>
public class ProfileBackupService : IProfileBackupService
{

    private readonly string _backupDirectory;

    public ProfileBackupService(string? backupDirectory = null)
    {
        // Produktion: Dokumente\FrameBouncer\Backups (portable EXE, zentrale
        // Benutzerdaten) – Test-Override bleibt möglich.
        _backupDirectory = backupDirectory ?? UserDataPaths.BackupsDirectory;
    }

    public string BackupDirectory => _backupDirectory;

    public IReadOnlyList<string> ListBackups()
    {
        try
        {
            if (!Directory.Exists(_backupDirectory)) return Array.Empty<string>();
            return Directory.GetFiles(_backupDirectory, "*.json")
                .OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public BackupCreatedResult CreateBackup(IReadOnlyList<GameProfile> profiles, DateTime? nowUtc = null, string? explicitPath = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        var backup = new ProfileBackupFile
        {
            FormatVersion = ProfileBackupFile.CurrentFormatVersion,
            CreatedAt = now,
            AppVersion = typeof(ProfileBackupService).Assembly.GetName().Version?.ToString() ?? string.Empty,
            Profiles = profiles.Select(BackupValidator.FromGameProfile).ToList()
        };

        var json = JsonSerializer.Serialize(backup, BackupValidator.BackupJsonOptions);

        // Vom Benutzer gewählter Pfad (SaveFileDialog, Überschreiben dort bestätigt):
        // direkt dorthin, atomar (Punkt 10).
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            AtomicFile.WriteAllText(explicitPath, json);
            return new BackupCreatedResult(explicitPath, Path.GetFileName(explicitPath), backup.Profiles.Count);
        }

        var fileName = BackupValidator.BuildBackupFileName(now);

        // Eindeutiger Name: falls in derselben Sekunde bereits existiert → " (2)", " (3)" …
        var path = Path.Combine(_backupDirectory, fileName);
        var stem = Path.GetFileNameWithoutExtension(fileName);
        for (var n = 2; File.Exists(path); n++)
        {
            fileName = $"{stem} ({n}).json";
            path = Path.Combine(_backupDirectory, fileName);
        }

        AtomicFile.WriteAllText(path, json);
        return new BackupCreatedResult(path, fileName, backup.Profiles.Count);
    }

    public BackupValidationResult ReadAndValidate(string path) => BackupValidator.ValidateFile(path);

    public IReadOnlyList<GameProfile> RestoreBackup(
        ProfileBackupFile backup, IReadOnlyList<GameProfile> currentProfiles, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;

        // Safety-Net (Punkt 7): bevor etwas überschrieben wird, die aktuelle
        // Konfiguration in ein eigenes Backup schreiben. Schlägt das fehl, wird
        // abgebrochen, statt die aktuelle Konfiguration zu riskieren.
        if (currentProfiles.Count > 0)
        {
            CreateBackup(currentProfiles, now);
        }

        var restored = backup.Profiles.Select(BackupValidator.ToGameProfile).ToList();
        return restored;
    }
}
