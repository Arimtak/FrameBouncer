using System.Globalization;
using System.Resources;

namespace FrameBouncer.Resources;

/// <summary>
/// Resource accessor for the application strings (Strings.resx = English default,
/// Strings.de.resx = German satellite). Written manually instead of relying on the
/// Visual Studio ResX designer generator, so `dotnet build` works everywhere.
/// Lookups always fall back to the neutral (English) resource and finally to the
/// key itself — a missing translation can never produce a blank string or crash.
/// </summary>
public static class Strings
{
    private static readonly ResourceManager ResourceManager =
        new("FrameBouncer.Resources.Strings", typeof(Strings).Assembly);

    /// <summary>All keys of the neutral (English) resource — used by the localization tests.</summary>
    public static IReadOnlyList<string> AllKeys { get; } = CollectKeys();

    public static string GetString(string key, CultureInfo? culture = null) =>
        ResourceManager.GetString(key, culture) ?? key;

    /// <summary>True if the key exists in the given culture's resource (or its fallbacks).</summary>
    public static bool HasKey(string key, CultureInfo culture) =>
        ResourceManager.GetResourceSet(culture, createIfNotExists: true, tryParents: true)?.GetString(key) is not null;

    private static IReadOnlyList<string> CollectKeys()
    {
        var set = ResourceManager.GetResourceSet(CultureInfo.InvariantCulture, createIfNotExists: true, tryParents: true);
        if (set is null) return Array.Empty<string>();
        return set.Cast<System.Collections.DictionaryEntry>().Select(e => (string)e.Key).OrderBy(k => k).ToArray();
    }
}