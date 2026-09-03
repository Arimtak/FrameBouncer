using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Reine Backup-Validierung (Backup-Spec Punkt 8/9): keine Dateisystem- oder
/// Service-Abhängigkeiten, vollständig unit-testbar. Lädt nichts blind –
/// syntaktisch gültiges JSON mit falschem Inhalt wird abgelehnt.
/// </summary>
public static class BackupValidator
{
    private const int MinFps = 1;
    private const int MaxFps = 1000;

    private static readonly string[] InvalidProcessNameChars =
        { "/", "\\", ":", "*", "?", "\"", "<", ">", "|" };

    /// <summary>Akzeptierte formatVersion-Werte. Aktuell nur 1 (Punkt 9).</summary>
    public static bool IsSupportedVersion(int version) => version == ProfileBackupFile.CurrentFormatVersion;

    /// <summary>
    /// Gemeinsame JSON-Optionen (Backup-Spec Punkt 4): camelCase-Schlüssel wie im
    /// Spec-Beispiel (formatVersion/processName/targetFps/enabled). Beim Lesen wird
    /// zusätzlich case-insensitive gematcht, damit auch leicht abweichende
    /// Schreibweisen (PascalCase) valide Dateien nicht ausschließen.
    /// </summary>
    public static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public static BackupValidationResult ValidateJson(string json)
    {
        ProfileBackupFile? backup;
        try
        {
            backup = JsonSerializer.Deserialize<ProfileBackupFile>(json, BackupJsonOptions);
        }
        catch (JsonException)
        {
            return BackupValidationResult.Fail("Backup konnte nicht gelesen werden. Grund: ungültiges JSON-Format.");
        }
        catch (Exception)
        {
            return BackupValidationResult.Fail("Backup konnte nicht gelesen werden. Grund: ungültiges Format.");
        }

        if (backup is null)
            return BackupValidationResult.Fail("Backup konnte nicht gelesen werden. Grund: ungültiges Format.");

        if (!IsSupportedVersion(backup.FormatVersion))
            return BackupValidationResult.Fail(
                $"Nicht unterstützte Backup-Version: {backup.FormatVersion} (unterstützt: {ProfileBackupFile.CurrentFormatVersion}).");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in backup.Profiles)
        {
            if (entry is null)
                return BackupValidationResult.Fail("Backup ungültig: leerer Profileintrag.");

            if (string.IsNullOrWhiteSpace(entry.ProcessName))
                return BackupValidationResult.Fail("Backup ungültig: Profil ohne Prozess-/EXE-Namen.");

            var name = entry.ProcessName.Trim();
            if (name.Length > 260 || InvalidProcessNameChars.Any(name.Contains))
                return BackupValidationResult.Fail($"Backup ungültig: ungültiger Dateiname \"{name}\".");

            if (entry.TargetFps < MinFps || entry.TargetFps > MaxFps)
                return BackupValidationResult.Fail(
                    $"Backup ungültig: ungültiger FPS-Wert {entry.TargetFps} für \"{name}\" (erlaubt {MinFps}–{MaxFps}).");

            if (!seen.Add(name))
                return BackupValidationResult.Fail(
                    $"Backup ungültig: doppeltes Profil für \"{name}\".");
        }

        return BackupValidationResult.Ok(backup);
    }

    /// <summary>ValidateJson für Dateien: lesbar? JSON? Inhalt valide? Jeder Fehler benannt (Punkt 15).</summary>
    public static BackupValidationResult ValidateFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return BackupValidationResult.Fail("Backup-Datei nicht gefunden.");

        string json;
        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception)
        {
            return BackupValidationResult.Fail("Backup-Datei konnte nicht gelesen werden.");
        }

        return ValidateJson(json);
    }

    /// <summary>
    /// Abbildung Backup-Eintrag → GameProfile. Ungültige Einträge sind bereits durch
    /// ValidateJson ausgeschlossen; Trim und Vergleichs-Normalisierung finden hier statt.
    /// </summary>
    public static GameProfile ToGameProfile(ProfileBackupEntry entry)
    {
        var now = DateTime.UtcNow;
        return new GameProfile
        {
            ProcessName = entry.ProcessName.Trim(),
            TargetFps = entry.TargetFps,
            IsEnabled = entry.Enabled,
            CreatedUtc = entry.CreatedUtc == default ? now : entry.CreatedUtc,
            UpdatedUtc = entry.UpdatedUtc == default ? now : entry.UpdatedUtc
        };
    }

    /// <summary>GameProfile → Backup-Eintrag (IsEnabled → Enabled, Datumswerte werden erhalten).</summary>
    public static ProfileBackupEntry FromGameProfile(GameProfile profile) => new()
    {
        ProcessName = profile.ProcessName,
        TargetFps = profile.TargetFps,
        Enabled = profile.IsEnabled,
        CreatedUtc = profile.CreatedUtc,
        UpdatedUtc = profile.UpdatedUtc
    };

    /// <summary>Eindeutiger, sortierbarer Backup-Dateiname (Punkt 5/15: Überschreiben vermeiden).</summary>
    public static string BuildBackupFileName(DateTime nowUtc) =>
        $"FrameBouncer-Profiles-{nowUtc.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.json";
}
