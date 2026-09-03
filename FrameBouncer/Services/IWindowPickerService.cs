namespace FrameBouncer.Services;

/// <summary>
/// Ergebnis einer Fenster-Picker-Operation.
/// </summary>
public sealed class WindowPickerResult
{
    public string ProcessName { get; init; } = string.Empty;
    public string ExeName { get; init; } = string.Empty;
    public string WindowTitle { get; init; } = string.Empty;
}

/// <summary>
/// Schnittstelle für die Fenster-Picker-Funktionalität.
/// Ermöglicht dem Benutzer ein Fenster per Mausklick auszuwählen.
/// </summary>
public interface IWindowPickerService
{
    /// <summary>
    /// Startet den Picker-Modus. Blockiert bis der Benutzer klickt oder abbricht.
    /// </summary>
    /// <returns>PickerResult oder null bei Abbruch/Fehler.</returns>
    WindowPickerResult? PickWindow();

    /// <summary>
    /// Prüft ob der aktuelle Prozess ein gültiges Benutzerfenster hat.
    /// </summary>
    bool IsValidUserWindow(IntPtr hWnd);
}
