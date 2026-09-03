namespace FrameBouncer.Services;

/// <summary>
/// WPF-Implementierung der Backup-Dateiauswahl. Enthält keinerlei Backup-Logik –
/// nur Dialoge (Punkt 14/18: Logik bleibt in Services, nicht in Code-Behind).
/// Voll qualifizierte Dialog-Typen, da das Projekt zusätzlich WinForms
/// (Tray-Icon) referenziert und die Namen sonst mehrdeutig wären.
/// </summary>
public class BackupFilePicker : IBackupFilePicker
{
    public string? PickSavePath(string suggestedFileName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedFileName,
            Filter = "FrameBouncer Profil-Backup (*.json)|*.json|Alle Dateien (*.*)|*.*",
            Title = "Profil-Backup speichern"
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? PickOpenPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "FrameBouncer Profil-Backup (*.json)|*.json|Alle Dateien (*.*)|*.*",
            Title = "Profil-Backup wiederherstellen",
            CheckFileExists = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
