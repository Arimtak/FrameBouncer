using System.Diagnostics;
using System.IO;
using FrameBouncer.Services;

namespace FrameBouncer.Internal;

/// <summary>
/// Elevations-Modus der portablen Single-EXE (früher FrameBouncer.ElevationHelper.exe).
/// Wird von RtssService ON DEMAND mit „runas“ gestartet, wenn der direkte Schreibzugriff
/// auf die RTSS-Profile verweigert wird:
///
///   FrameBouncer.exe --elevated-helper writeLimit    &lt;installPath&gt; &lt;processName&gt; &lt;targetFps&gt;
///   FrameBouncer.exe --elevated-helper writeTemplate &lt;installPath&gt; &lt;processName&gt; &lt;targetFps&gt;
///
/// writeLimit    : aktives RTSS-Profil  -> &lt;installPath&gt;\Profiles\&lt;processName&gt;.cfg
///                 (wirkt auf laufende/neue Prozesse, kein RTSS-Neustart nötig)
/// writeTemplate : GUI-Vorlage          -> &lt;installPath&gt;\ProfileTemplates\&lt;processName&gt;.cfg
///
/// Exit-Codes: 0 = Erfolg, 1 = Schreibfehler, 2 = ungültige Argumente
/// </summary>
public static class ElevatedHelperMode
{
    public static int Run(string[] args)
    {
        // --lang=<code> wird von RtssService mitgegeben (App.xaml.cs wendet es an)
        args = args.Where(a => !a.StartsWith("--lang=", StringComparison.Ordinal)).ToArray();
        if (args.Length < 3)
        {
            Console.Error.WriteLine(Localization.T("Helper.Usage"));
            return 2;
        }

        int argIndex = 0;
        string operation = args[0] is "writeLimit" or "writeTemplate" ? args[argIndex++] : "writeLimit";

        if (args.Length - argIndex < 3)
        {
            Console.Error.WriteLine(Localization.T("Helper.Usage"));
            return 2;
        }

        string installPath = args[argIndex];
        string processName = args[argIndex + 1];

        if (!int.TryParse(args[argIndex + 2], out int targetFps))
        {
            Console.Error.WriteLine(Localization.TFmt("Helper.InvalidFpsFmt", args[argIndex + 2]));
            return 2;
        }

        try
        {
            if (operation == "writeTemplate")
            {
                // LEGACY-Pfad: GUI-Vorlage (keine Wirkung auf laufende Spiele)
                RtssProfileWriter.SetFpsLimit(installPath, processName, targetFps);
                Console.WriteLine(Localization.TFmt("Helper.TemplateWrittenFmt", processName, targetFps));
            }
            else
            {
                // AKTIVER Pfad: Profiles\<exe>.cfg – das liest RTSS wirklich ein
                RtssProfileWriter.SetProfileLimit(installPath, processName, targetFps);
                Console.WriteLine(Localization.TFmt("Helper.ProfileWrittenFmt", processName, targetFps));

                // Nach dem ersten erfolgreichen Schreibvorgang die Profiles-ACL einmalig für
                // den aktuellen Benutzer erweitern (best effort). Danach kann FrameBouncer
                // (non-elevated) direkt schreiben – Apply UND Exit-Reset ohne weitere UAC.
                // Reversible Systemänderung, nur auf den RTSS-Profiles-Ordner beschränkt.
                TryGrantProfileWriteAccess(installPath);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Localization.TFmt("Helper.WriteErrorFmt", ex.Message));
            return 1;
        }
    }

    /// <summary>
    /// Erweitert die ACL des RTSS-Profiles-Ordners um "Modify" für den aktuellen
    /// (elevierten) Benutzer – mit Vererbung auf vorhandene Dateien (icacls /T).
    /// Läuft NUR hier im bereits elevierten Modus; Fehler sind non-fatal.
    /// </summary>
    private static void TryGrantProfileWriteAccess(string installPath)
    {
        try
        {
            string profilesDir = Path.Combine(installPath, "Profiles");
            if (!Directory.Exists(profilesDir)) return;

            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            string account = identity.Name; // z.B. DESKTOP-XXXX\Arimtak

            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "icacls.exe",
                Arguments = $"\"{profilesDir}\" /grant \"{account}:(OI)(CI)M\" /T /C /Q",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            });
            if (p is null) return;
            if (!p.WaitForExit(10_000))
            {
                Console.Error.WriteLine(Localization.T("Helper.AclTimeout"));
                return;
            }
            Console.WriteLine(Localization.TFmt("Helper.AclGrantedFmt", account, profilesDir));
        }
        catch (Exception ex)
        {
            // Best effort – ohne ACL läuft der bisherige Helper-Fallback weiter
            Console.Error.WriteLine(Localization.TFmt("Helper.AclFailedFmt", ex.Message));
        }
    }
}