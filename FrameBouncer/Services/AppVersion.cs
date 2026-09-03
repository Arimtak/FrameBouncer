using System.Reflection;

namespace FrameBouncer.Services;

/// <summary>
/// Zentrale Versionsquelle (Update-Spec Punkt 3): Eine einzige Produktversion
/// (aus &lt;Version&gt; im csproj → AssemblyInformationalVersion), konsistent für
/// Assembly-, Produkt-, FileVersion, Release-Asset und Update-Checker.
/// </summary>
public static class AppVersion
{
    /// <summary>Aktuelle Produktversion der App (z. B. "1.0.0").</summary>
    public static string Current { get; } = ReadCurrent();

    /// <summary>
    /// Parst eine Versionszeichenkette tolerant ("v1.1.0" → 1.1.0.0).
    /// Null bei ungültiger Eingabe – niemals werfend.
    /// </summary>
    public static Version? Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var s = value.Trim();
        if (s.Length > 0 && (s[0] == 'v' || s[0] == 'V')) s = s[1..];
        return Version.TryParse(s, out var parsed) ? parsed : null;
    }

    private static string ReadCurrent()
    {
        try
        {
            var attr = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var v = attr?.InformationalVersion ?? "1.0.0";
            // InformationalVersion kann einen "+build"-Suffix tragen (SourceLink) – kappen.
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
        catch
        {
            return "1.0.0";
        }
    }
}