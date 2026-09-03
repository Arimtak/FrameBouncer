namespace FrameBouncer.Services;

/// <summary>
/// Dateiauswahl für Backup/Restore (Punkt 14). Abstrahiert SaveFileDialog/
/// OpenFileDialog, damit die VM testbar bleibt und keine Backup-Logik in der
/// Code-Behind landet (Punkt 18).
/// </summary>
public interface IBackupFilePicker
{
    /// <summary>
    /// Öffnet einen "Speichern unter"-Dialog für ein neues Backup.
    /// Gibt null zurück, wenn der Benutzer abbricht.
    /// </summary>
    string? PickSavePath(string suggestedFileName);

    /// <summary>
    /// Öffnet einen "Datei öffnen"-Dialog zum Auswählen eines vorhandenen Backups.
    /// Gibt null zurück, wenn der Benutzer abbricht.
    /// </summary>
    string? PickOpenPath();
}
