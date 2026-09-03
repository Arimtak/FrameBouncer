using System;
using System.IO;
using System.Text.Json;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

public class JsonSettingsService : ISettingsService
{
    // Frühere Speicherorte – werden einmalig in Dokumente\FrameBouncer migriert:
    // 1) neben der EXE (alte portable Variante), 2) %APPDATA%\FrameBouncer
    private static readonly string[] DefaultLegacySettingsPaths =
    [
        Path.Combine(AppContext.BaseDirectory, "settings.json"),
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "FrameBouncer", "settings.json")
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;
    private readonly string[] _legacySettingsPaths;
    private readonly bool _legacyPathsExplicit;

    /// <summary>
    /// Optionaler Verzeichnis-Override (für Tests) und optionale Legacy-Quellen.
    /// Produktion: Dokumente\FrameBouncer (portable EXE, alle Benutzerdaten an
    /// einem festen Ort); frühere Speicherorte werden einmalig migriert.
    /// </summary>
    public JsonSettingsService(string? settingsDirectory = null, IEnumerable<string>? legacySettingsDirectories = null)
    {
        _settingsDirectory = settingsDirectory ?? UserDataPaths.DataDirectory;
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
        _legacyPathsExplicit = legacySettingsDirectories is not null;
        _legacySettingsPaths = legacySettingsDirectories
            ?.Select(d => Path.Combine(d, "settings.json"))
            .ToArray()
            ?? DefaultLegacySettingsPaths;
    }

    public string SettingsPath => _settingsPath;

    public AppSettings Load()
    {
        try
        {
            MigrateLegacySettingsIfNeeded();

            if (File.Exists(_settingsPath))
            {
                var json = File.ReadAllText(_settingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
            }
        }
        catch
        {
            // Bei Lesefehler Defaults verwenden
        }
        return new AppSettings();
    }

    public void Save(AppSettings settings)
    {
        try
        {
            // Atomar schreiben (Backup-Spec Punkt 10): Temp-Datei → Replace.
            // Stirbt die App während des Schreibens, bleibt settings.json vollständig.
            AtomicFile.WriteAllText(_settingsPath, JsonSerializer.Serialize(settings, JsonOptions));
        }
        catch
        {
            // Stille Fehlerbehandlung – Settings sind nicht kritisch
        }
    }

    private void MigrateLegacySettingsIfNeeded()
    {
        // Migration nur auf dem echten Produktionspfad (Dokumente\FrameBouncer) oder
        // wenn der Aufrufer explizit Legacy-Quellen angibt (Tests) – bei bloßen
        // Test-Overrides bleibt die echte Benutzerkonfiguration unangetastet.
        if (!_legacyPathsExplicit &&
            !string.Equals(_settingsPath, UserDataPaths.SettingsPath, StringComparison.OrdinalIgnoreCase))
            return;

        try
        {
            foreach (var legacyPath in _legacySettingsPaths)
            {
                if (string.IsNullOrWhiteSpace(legacyPath) || !File.Exists(legacyPath)) continue;

                Directory.CreateDirectory(_settingsDirectory);
                if (!File.Exists(_settingsPath))
                {
                    File.Copy(legacyPath, _settingsPath, overwrite: false);
                }
                File.Delete(legacyPath);
            }
        }
        catch
        {
            // Migration ist best-effort – die App startet auch ohne Migration
        }
    }
}
