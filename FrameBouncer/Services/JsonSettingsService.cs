using System;
using System.IO;
using System.Text.Json;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

public class JsonSettingsService : ISettingsService
{
    private static readonly string DefaultSettingsDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "FrameBouncer");

    // Alte Settings-Datei neben der EXE – wird einmalig migriert
    private static readonly string LegacySettingsPath = Path.Combine(
        AppContext.BaseDirectory, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsDirectory;
    private readonly string _settingsPath;

    /// <summary>
    /// Optionaler Verzeichnis-Override (für Tests). Produktion: %APPDATA%\FrameBouncer.
    /// </summary>
    public JsonSettingsService(string? settingsDirectory = null)
    {
        _settingsDirectory = settingsDirectory ?? DefaultSettingsDirectory;
        _settingsPath = Path.Combine(_settingsDirectory, "settings.json");
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
        try
        {
            if (!File.Exists(LegacySettingsPath)) return;

            Directory.CreateDirectory(_settingsDirectory);
            if (!File.Exists(_settingsPath))
            {
                File.Copy(LegacySettingsPath, _settingsPath, overwrite: false);
            }
            File.Delete(LegacySettingsPath);
        }
        catch
        {
            // Migration ist best-effort
        }
    }
}
