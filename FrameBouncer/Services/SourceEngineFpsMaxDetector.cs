using System.IO;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Proof-of-Concept-Detector: **Source-Engine** (Valve, Source 1) `fps_max`.
///
/// Verifizierbare, dokumentierte Quelle (Spec Punkt 5/16):
/// - Engine-Signatur: `GameInfo.txt` im Install-Verzeichnis der EXE (Source-1-Spiele
///   wie HL2, CS:S/CS:GO, TF2, Portal 1/2, L4D 1/2, GMod …). Source 2 (CS2, Dota 2,
///   HL:Alyx) nutzt `gameinfo.gi` → wird bewusst NICHT erkannt (→ Unknown).
/// - Einstellung: `fps_max &lt;n&gt;` in `cfg\autoexec.cfg` bzw. `cfg\config.cfg`
///   (dokumentiertes Source-Kommando). `fps_max 0` = ausdrücklich unbegrenzt
///   (→ Off/Disabled), `fps_max n` (n&gt;0) = aktiver Cap (→ On).
/// - Vorrang: `autoexec.cfg` wird in Source nach `config.cfg` ausgeführt und
///   überschreibt dessen Wert → wird zuerst geprüft.
///
/// Konservativ (Spec: „Lieber Unknown als eine falsche Zahl“):
/// - Kein `fps_max`-Schlüssel in einer gelesenen Datei → Unknown (der Default
///   variiert pro Spiel und ist ohne Referenztabelle nicht sicher).
/// - Ungültiger/negativer Wert (`fps_max abc`, `fps_max -1`) → Unknown.
/// - Kein `cfg\`-Ordner bzw. keine lesbare cfg-Datei → Unavailable.
///
/// Ausschließlich lesend: keine Datei wird verändert oder erzeugt.
/// </summary>
public class SourceEngineFpsMaxDetector : IInGameLimiterDetector
{
    private static readonly string[] ConfigFilesInPrecedenceOrder =
    {
        Path.Combine("cfg", "autoexec.cfg"),
        Path.Combine("cfg", "config.cfg")
    };

    public bool CanHandle(GameContext context)
    {
        try
        {
            if (string.IsNullOrEmpty(context.InstallDirectory)) return false;
            return File.Exists(Path.Combine(context.InstallDirectory, "GameInfo.txt"));
        }
        catch
        {
            return false;
        }
    }

    public LimiterState Detect(GameContext context)
    {
        try
        {
            if (string.IsNullOrEmpty(context.InstallDirectory)) return LimiterState.Unavailable(LimiterSource.InGame);

            var cfgDir = Path.Combine(context.InstallDirectory, "cfg");
            if (!Directory.Exists(cfgDir)) return LimiterState.Unavailable(LimiterSource.InGame);

            bool anyFileRead = false;
            foreach (var relative in ConfigFilesInPrecedenceOrder)
            {
                var path = Path.Combine(context.InstallDirectory, relative);
                if (!File.Exists(path)) continue;

                string content;
                try
                {
                    content = File.ReadAllText(path);
                    anyFileRead = true;
                }
                catch
                {
                    // Unlesbar (Zugriff verweigert) → nächste Datei versuchen.
                    continue;
                }

                var parsed = TryParseFpsMax(content);
                if (!parsed.Found) continue; // Schlüssel fehlt → nächste Datei

                if (!parsed.Valid) return LimiterState.Unknown(LimiterSource.InGame);
                if (parsed.Value < 0) return LimiterState.Unknown(LimiterSource.InGame);
                if (parsed.Value == 0) return LimiterState.Off(LimiterSource.InGame); // ausdrücklich unbegrenzt

                return LimiterState.On(LimiterSource.InGame, parsed.Value);
            }

            // cfg-Ordner vorhanden: Dateien lesbar, aber kein fps_max-Schlüssel
            // (Default variiert pro Spiel) → ehrlich Unknown; gar nichts lesbar → Unavailable.
            return anyFileRead
                ? LimiterState.Unknown(LimiterSource.InGame)
                : LimiterState.Unavailable(LimiterSource.InGame);
        }
        catch
        {
            return LimiterState.Unknown(LimiterSource.InGame);
        }
    }

    private readonly record struct FpsMaxParse(int Value, bool Found, bool Valid)
    {
        public static FpsMaxParse NotFound => new(0, false, false);
        public static FpsMaxParse Invalid => new(0, true, false);
        public static FpsMaxParse ValidValue(int value) => new(value, true, true);
    }

    /// <summary>
    /// Sucht das `fps_max`-Kommando zeilenweise (Kommentare `//` ignoriert).
    /// Liefert NotFound (Schlüssel fehlt), Invalid (Schlüssel da, Wert kaputt)
    /// oder Valid (Wert geparst).
    /// </summary>
    private static FpsMaxParse TryParseFpsMax(string content)
    {
        foreach (var rawLine in content.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith('#')) continue;

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || !parts[0].Equals("fps_max", StringComparison.OrdinalIgnoreCase)) continue;

            var valueToken = parts[1].Trim('"');
            return int.TryParse(valueToken, out var value)
                ? FpsMaxParse.ValidValue(value)
                : FpsMaxParse.Invalid;
        }

        return FpsMaxParse.NotFound;
    }
}