using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FrameBouncer.Services;

/// <summary>
/// Gemeinsame Logik zum Schreiben der RTSS-Profil-Dateien.
/// Wird von der App genutzt, wenn die Rechte ausreichen, und vom ElevationHelper,
/// der elevated läuft. Rein funktional und ohne UI-Abhängigkeiten, gut testbar.
///
/// WICHTIG (empirisch verifiziert): RTSS lädt aktive Limiter-Einstellungen aus
/// &lt;installPath&gt;\Profiles\&lt;processName&gt;.cfg. ProfileTemplates\ ist nur die Vorlage
/// für neue, in der RTSS-GUI erstellte Profile – Einträge dort haben KEINEN Einfluss
/// auf laufende Spiele. Ein wirksames Limit wird deshalb NACH Profiles\ geschrieben
/// (read-modify-write, alle anderen Schlüssel bleiben unangetastet).
/// </summary>
public static class RtssProfileWriter
{
    /// <summary>Legacypfad (nur Vorlagen, ohne Wirkung auf laufende Spiele).</summary>
    public static string GetProfileTemplatesPath(string installPath) =>
        Path.Combine(installPath, "ProfileTemplates");

    /// <summary>Aktiver RTSS-Profilspeicher – hier liest RTSS die Limits her.</summary>
    public static string GetProfilesPath(string installPath) =>
        Path.Combine(installPath, "Profiles");

    /// <summary>
    /// Setzt das FPS-Limit in der AKTIVEN RTSS-Profil-Datei
    /// &lt;installPath&gt;\Profiles\&lt;processName&gt;.cfg.
    /// Existiert die Datei nicht, wird eine minimale, vollständige Profil-Datei
    /// erzeugt (inkl. [Hooking] EnableHooking=1). Existiert sie, wird ausschließlich
    /// der Limit-Schlüssel ersetzt – alle anderen Sektionen/Schlüssel (OSD,
    /// Statistics, Hooking, Font …) bleiben erhalten. targetFps=0 deaktiviert
    /// das Limit (RTSS-Konvention).
    /// </summary>
    public static void SetProfileLimit(string installPath, string processName, int targetFps)
    {
        string profileDir = GetProfilesPath(installPath);
        Directory.CreateDirectory(profileDir);

        string profilePath = Path.Combine(profileDir, $"{processName}.cfg");
        int limit = Math.Max(0, targetFps);

        string content;
        if (File.Exists(profilePath))
        {
            // Read-modify-write: Nur den Limit-Schlüssel in [Framerate] ersetzen.
            var lines = File.ReadAllLines(profilePath);
            bool inFramerateSection = false;
            bool found = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    inFramerateSection = trimmed.Equals("[Framerate]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inFramerateSection && trimmed.StartsWith("Limit", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("LimitDenominator", StringComparison.OrdinalIgnoreCase) &&
                    !trimmed.StartsWith("LimitTime", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Limit={limit}";
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var newLines = new List<string>(lines);
                newLines.Add("");
                newLines.Add("[Framerate]");
                newLines.Add($"Limit={limit}");
                newLines.Add("LimitDenominator=1");
                lines = newLines.ToArray();
            }

            content = string.Join("\r\n", lines) + "\r\n";
        }
        else
        {
            // Minimale, vollständige Profil-Datei im Stil bestehender Profiles\*.cfg:
            // Hooking aktiviert, Limit gesetzt, Standard-Keys.
            content =
                "[OSD]\r\n" +
                "EnableOSD=1\r\n" +
                "EnableBgnd=1\r\n" +
                "[Statistics]\r\n" +
                "FramerateAveragingInterval=1000\r\n" +
                "[Framerate]\r\n" +
                $"Limit={limit}\r\n" +
                "LimitDenominator=1\r\n" +
                "SyncLimiter=0\r\n" +
                "PassiveWait=1\r\n" +
                "[Hooking]\r\n" +
                "EnableHooking=1\r\n" +
                "HookDirect3D9=1\r\n" +
                "HookDXGI=1\r\n" +
                "HookDirect3D12=1\r\n" +
                "HookOpenGL=1\r\n" +
                "HookVulkan=1\r\n";
        }

        File.WriteAllText(profilePath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// LEGACY: Schreibt nur die GUI-Vorlage nach ProfileTemplates\ (kein Einfluss auf
    /// laufende Spiele). Wird zusätzlich gepflegt, damit in der RTSS-GUI angelegte
    /// Profile den zuletzt gewünschten Limit-Wert als Startwert vorfinden.
    /// </summary>
    public static void SetFpsLimit(string installPath, string processName, int targetFps)
    {
        string profileDir = GetProfileTemplatesPath(installPath);
        Directory.CreateDirectory(profileDir);

        string profilePath = Path.Combine(profileDir, $"{processName}.cfg");

        string content;
        if (File.Exists(profilePath))
        {
            var lines = File.ReadAllLines(profilePath);
            bool inFramerateSection = false;
            bool found = false;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    inFramerateSection = line.Equals("[Framerate]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inFramerateSection && line.StartsWith("Limit", StringComparison.OrdinalIgnoreCase))
                {
                    lines[i] = $"Limit\t\t\t\t= {targetFps}";
                    found = true;
                    break;
                }
            }

            if (!found)
            {
                var newLines = new List<string>(lines);
                newLines.Add("");
                newLines.Add("[Framerate]");
                newLines.Add($"Limit\t\t\t\t= {targetFps}");
                lines = newLines.ToArray();
            }

            content = string.Join("\n", lines);
        }
        else
        {
            content = "[Hooking]\nCBTFlags\t\t\t\t= 0\n\n[Framerate]\nLimit\t\t\t\t= " + targetFps + "\n";
        }

        File.WriteAllText(profilePath, content);
    }
}
