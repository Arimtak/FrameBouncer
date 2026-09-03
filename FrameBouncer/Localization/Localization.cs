using System.ComponentModel;
using System.Globalization;
using FrameBouncer.Resources;

namespace FrameBouncer;

/// <summary>
/// Central localization facade. All user-visible strings go through this class:
///
///   • XAML:  Text="{Binding [Ui.ApplyButton], Source={x:Static loc:Localization.Instance}}"
///   • Code:  Localization.T("Status.Ready") or Localization.TFmt("Status.PresetFmt", fps)
///
/// Lookup chain (spec §9): selected language → English fallback → the key itself,
/// so a missing translation can never show a blank string or crash.
///
/// Language is an explicit application setting ("en"/"de"); the Windows system
/// language is never auto-detected (§10). Switching raises PropertyChanged(null),
/// which refreshes every bound element immediately (no restart needed).
/// </summary>
public sealed class Localization : INotifyPropertyChanged
{
    /// <summary>Single binding source for XAML indexer bindings.</summary>
    public static Localization Instance { get; } = new();

    /// <summary>Raised after the language changed — used by code-behind (tray menu etc.).</summary>
    public static event Action? LanguageChanged;

    private Localization() { }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Selected language code: "en" (default) or "de".</summary>
    public static string LanguageCode { get; private set; } = "en";

    /// <summary>Culture used for number/date formatting of the selected language.</summary>
    public static CultureInfo DisplayCulture { get; private set; } = CultureInfo.GetCultureInfo("en-US");

    /// <summary>XAML indexer: {Binding [key], Source={x:Static loc:Localization.Instance}}.</summary>
    public string this[string key] => T(key);

    /// <summary>Localized string for the selected language (English fallback, never blank).</summary>
    public static string T(string key) =>
        Strings.GetString(key, DisplayCulture) is { } value && value != key
            ? value
            : Strings.GetString(key, CultureInfo.GetCultureInfo("en"));

    /// <summary>Localized string with format arguments (e.g. "Preset: {0} FPS").</summary>
    public static string TFmt(string key, params object?[] args)
    {
        try
        {
            return string.Format(DisplayCulture, T(key), args);
        }
        catch (FormatException)
        {
            // A malformed format string must never crash the UI.
            return T(key);
        }
    }

    /// <summary>
    /// Switches the application language. Only "de" selects German, everything
    /// else falls back to English. Sets CurrentUICulture/CurrentCulture for the
    /// whole app (without touching the Windows system language) and raises
    /// PropertyChanged so all XAML bindings refresh immediately.
    /// </summary>
    public static void SetLanguage(string? code)
    {
        string next = string.Equals(code, "de", StringComparison.OrdinalIgnoreCase) ? "de" : "en";
        LanguageCode = next;
        DisplayCulture = next == "de"
            ? CultureInfo.GetCultureInfo("de-DE")
            : CultureInfo.GetCultureInfo("en-US");
        CultureInfo.CurrentUICulture = DisplayCulture;
        CultureInfo.CurrentCulture = DisplayCulture;

        // null = "all properties changed" → every indexer binding re-evaluates.
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(null));
        LanguageChanged?.Invoke();
    }
}