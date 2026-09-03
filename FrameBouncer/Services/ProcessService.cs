using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameBouncer.Services;

public class ProcessService : IProcessService
{
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    private static readonly HashSet<string> SystemProcessNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "csrss", "services", "dwm", "RuntimeBroker",
        "SearchIndexer", "SearchHost", "ShellExperienceHost",
        "StartMenuExperienceHost", "TextInputHost", "ctfmon",
        "conhost", "dllhost", "lsass", "lsm", "smss", "wininit",
        "winlogon", "FontDriverHost", "sihost", "taskhostw",
        "WmiPrvSE", "WUDFHost", "spoolsv", "SecurityHealthService",
        "MsMpEng", "NisSrv", "SearchProtocolHost", "SearchFilterHost"
    };

    public IReadOnlyList<string> GetRunningProcesses()
    {
        try
        {
            return Process.GetProcesses()
                .Where(p =>
                {
                    try
                    {
                        if (p.Id <= 4) return false;
                        if (string.IsNullOrEmpty(p.ProcessName)) return false;
                        if (SystemProcessNames.Contains(p.ProcessName)) return false;

                        var hWnd = p.MainWindowHandle;
                        if (hWnd == IntPtr.Zero) return false;
                        if (!IsWindowVisible(hWnd)) return false;

                        var title = GetWindowTitle(hWnd);
                        if (string.IsNullOrWhiteSpace(title)) return false;

                        return true;
                    }
                    catch { return false; }
                })
                .Select(p =>
                {
                    try { return p.ProcessName + ".exe"; }
                    catch { return null!; }
                })
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
