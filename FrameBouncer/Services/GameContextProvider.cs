using System.Diagnostics;

namespace FrameBouncer.Services;

/// <summary>
/// Ermittelt den Laufzeit-Kontext eines Spiels aus dem EXE-Namen
/// (Spec Punkt 3/6): `Process.GetProcessesByName` → `MainModule.FileName`.
/// Rein lesend, nie werfend — jeder Fehler (Prozess beendet, Zugriff
/// verweigert, keine Rechte auf MainModule) führt zu null.
/// </summary>
public class GameContextProvider : IGameContextProvider
{
    public GameContext? GetContext(string? processName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(processName)) return null;

            var name = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            Process? process = null;
            try
            {
                process = Process.GetProcessesByName(name).FirstOrDefault(p => p.Id > 4);
            }
            catch
            {
                return null;
            }

            if (process is null) return null;

            using (process)
            {
                string? path = null;
                try
                {
                    path = process.MainModule?.FileName;
                }
                catch
                {
                    // Zugriff verweigert (z. B. fremde Elevation) → kein Kontext.
                }

                if (string.IsNullOrEmpty(path)) return null;

                return new GameContext
                {
                    ProcessName = processName,
                    ProcessId = process.Id,
                    ExecutablePath = path,
                    InstallDirectory = System.IO.Path.GetDirectoryName(path)
                };
            }
        }
        catch
        {
            return null;
        }
    }
}