using System.Diagnostics;
using FrameBouncer.Services;

// FrameBouncer.ElevationHelper
// Aufruf (neu):
//   FrameBouncer.ElevationHelper.exe writeLimit    <installPath> <processName> <targetFps>
//   FrameBouncer.ElevationHelper.exe writeTemplate <installPath> <processName> <targetFps>
// Legacy-Aufruf (ohne operation) = writeLimit:
//   FrameBouncer.ElevationHelper.exe <installPath> <processName> <targetFps>
//
// writeLimit    : aktives RTSS-Profil  -> <installPath>\Profiles\<processName>.cfg
//                 (wirkt auf laufende/neue Prozesse, kein RTSS-Neustart nötig)
// writeTemplate : GUI-Vorlage          -> <installPath>\ProfileTemplates\<processName>.cfg
//
// Exit-Codes: 0 = Erfolg, 1 = Schreibfehler, 2 = ungültige Argumente

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: FrameBouncer.ElevationHelper [writeLimit|writeTemplate] <installPath> <processName> <targetFps>");
    return 2;
}

int argIndex = 0;
string operation = args[0] is "writeLimit" or "writeTemplate" ? args[argIndex++] : "writeLimit";

if (args.Length - argIndex < 3)
{
    Console.Error.WriteLine("Usage: FrameBouncer.ElevationHelper [writeLimit|writeTemplate] <installPath> <processName> <targetFps>");
    return 2;
}

string installPath = args[argIndex];
string processName = args[argIndex + 1];

if (!int.TryParse(args[argIndex + 2], out int targetFps))
{
    Console.Error.WriteLine($"Ungültiges FPS-Limit: {args[argIndex + 2]}");
    return 2;
}

try
{
    if (operation == "writeTemplate")
    {
        // LEGACY-Pfad: GUI-Vorlage (keine Wirkung auf laufende Spiele)
        RtssProfileWriter.SetFpsLimit(installPath, processName, targetFps);
        Console.WriteLine($"Vorlage geschrieben: {processName} = {targetFps} FPS");
    }
    else
    {
        // AKTIVER Pfad: Profiles\<exe>.cfg – das liest RTSS wirklich ein
        RtssProfileWriter.SetProfileLimit(installPath, processName, targetFps);
        Console.WriteLine($"Profil geschrieben: {processName} = {targetFps} FPS");

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
    Console.Error.WriteLine($"Fehler beim Schreiben des Profils: {ex.Message}");
    return 1;
}

/// <summary>
/// Erweitert die ACL des RTSS-Profiles-Ordners um "Modify" für den aktuellen
/// (elevierten) Benutzer – mit Vererbung auf vorhandene Dateien (icacls /T).
/// Läuft NUR hier im bereits elevierten Helper; Fehler sind non-fatal.
/// </summary>
static void TryGrantProfileWriteAccess(string installPath)
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
            Console.Error.WriteLine("ACL-Erweiterung: Timeout");
            return;
        }
        Console.WriteLine($"ACL erweitert: {account} darf künftig direkt schreiben ({profilesDir})");
    }
    catch (Exception ex)
    {
        // Best effort – ohne ACL läuft der bisherige Helper-Fallback weiter
        Console.Error.WriteLine($"ACL-Erweiterung nicht möglich: {ex.Message}");
    }
}
