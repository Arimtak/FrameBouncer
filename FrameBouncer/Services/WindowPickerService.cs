using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameBouncer.Services;

public sealed class WindowPickerService : IWindowPickerService
{
    public bool IsValidUserWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return false;
        if (!NativeMethods.IsWindowVisible(hWnd)) return false;

        var title = GetWindowText(hWnd);
        if (string.IsNullOrWhiteSpace(title)) return false;

        return true;
    }

    public WindowPickerResult? PickWindow()
    {
        try
        {
            if (!NativeMethods.GetCursorPos(out var point))
                return null;

            var hWnd = NativeMethods.WindowFromPoint(point);
            if (hWnd == IntPtr.Zero) return null;

            hWnd = NativeMethods.GetAncestor(hWnd, NativeMethods.GA_ROOTOWNER);
            if (hWnd == IntPtr.Zero) return null;

            if (!IsValidUserWindow(hWnd))
                return null;

            NativeMethods.GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == 0) return null;

            var process = Process.GetProcessById((int)pid);
            if (process is null)
                return null;

            var processName = process.ProcessName;
            var exeName = processName + ".exe";
            var windowTitle = GetWindowText(hWnd);

            return new WindowPickerResult
            {
                ProcessName = processName,
                ExeName = exeName,
                WindowTitle = windowTitle
            };
        }
        catch
        {
            return null;
        }
    }

    private static string GetWindowText(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
        return sb.ToString();
    }
}
