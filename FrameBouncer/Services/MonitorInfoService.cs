using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace FrameBouncer.Services;

/// <summary>
/// Echte Monitor-/Refreshrate-Erkennung über die Windows-Display-Konfiguration
/// (EnumDisplayMonitors + EnumDisplaySettings mit ENUM_CURRENT_SETTINGS).
/// Keine EDID-/Namens-Raterei – es zählt ausschließlich der aktuell aktive
/// Displaymode. Lesend und rein diagnostisch.
///
/// Testbarkeit: Statt nativer Aufrufe werden delegate-Fabriken genutzt
/// (monitors/enumerate/displayMode), damit die Hz-Validierung, Zielmonitor-
/// Auswahl und "Unbekannt"-Behandlung ohne echten Bildschirm getestet werden
/// können. Ungültige Werte (0, negativ, absurde Zahlen) führen NIEMALS zu
/// "0 Hz", sondern zu IsAvailable=false → UI zeigt "Unbekannt".
/// </summary>
public class MonitorInfoService : IMonitorInfoService
{
    private const int ENUM_CURRENT_SETTINGS = -1;
    private const int CCHDEVICENAME = 32;

    // --- Win32-P/Invoke -------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// Öffentliches Bounds-Format für NativeMonitor (testbar, ohne das private
    /// Win32-RECT preiszugeben).
    /// </summary>
    public readonly record struct MonitorBounds(int Left, int Top, int Right, int Bottom);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEXW
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsW(string deviceName, int modeNum, ref DEVMODEW devMode);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr data);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr data);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    // --- Testbare Fabriken ----------------------------------------------

    /// <summary>Ein Windows-Monitor-Handle mit Kontext (für Fabrik-Ergebnisse).</summary>
    public readonly record struct NativeMonitor(
        IntPtr Handle,
        string DeviceName,
        string MonitorDeviceId,
        bool IsPrimary,
        MonitorBounds Bounds);

    /// <summary>Enumeriert die nativen Monitor-Handles (in Tests ersetzbar).</summary>
    private readonly Func<IReadOnlyList<NativeMonitor>> _enumerateMonitors;

    /// <summary>Liest den AKTUELLEN Displaymode eines Geräts (in Tests ersetzbar).</summary>
    private readonly Func<string, (int Width, int Height, int RefreshHz)?> _readCurrentDisplayMode;

    public MonitorInfoService()
        : this(EnumerateMonitorsNative, ReadCurrentDisplayModeNative)
    {
    }

    /// <summary>Test-Konstruktor mit injizierten Fabriken.</summary>
    public MonitorInfoService(
        Func<IReadOnlyList<NativeMonitor>> enumerateMonitors,
        Func<string, (int Width, int Height, int RefreshHz)?> readCurrentDisplayMode)
    {
        _enumerateMonitors = enumerateMonitors;
        _readCurrentDisplayMode = readCurrentDisplayMode;
    }

    // --- Interface-Implementierung ---------------------------------------

    public IReadOnlyList<MonitorInfo> GetMonitors()
    {
        var result = new List<MonitorInfo>();
        try
        {
            foreach (var native in _enumerateMonitors())
            {
                result.Add(BuildMonitorInfo(native));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] GetMonitors failed: {ex.Message}");
        }
        return result;
    }

    public MonitorInfo? GetPrimaryMonitor()
    {
        try
        {
            foreach (var native in _enumerateMonitors())
            {
                if (native.IsPrimary) return BuildMonitorInfo(native);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] GetPrimaryMonitor failed: {ex.Message}");
        }
        return null;
    }

    public MonitorInfo? GetMonitorForProcess(string processName)
    {
        if (string.IsNullOrWhiteSpace(processName)) return null;

        try
        {
            string baseName = processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? processName[..^4]
                : processName;

            IntPtr bestHwnd = IntPtr.Zero;
            EnumWindows((hWnd, _) =>
            {
                if (!IsWindowVisible(hWnd)) return true;
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0) return true;

                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    if (!proc.ProcessName.Equals(baseName, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    return true; // Prozess verschwunden → Fenster ignorieren
                }

                // Hauptfenster bevorzugen (größtes sichtbares Fenster)
                GetWindowRect(hWnd, out var rect);
                int area = Math.Abs(rect.Right - rect.Left) * Math.Abs(rect.Bottom - rect.Top);
                int bestArea = 0;
                if (bestHwnd != IntPtr.Zero)
                {
                    GetWindowRect(bestHwnd, out var bestRect);
                    bestArea = Math.Abs(bestRect.Right - bestRect.Left) * Math.Abs(bestRect.Bottom - bestRect.Top);
                }
                if (bestHwnd == IntPtr.Zero || area > bestArea)
                {
                    bestHwnd = hWnd;
                }
                return true;
            }, IntPtr.Zero);

            return bestHwnd != IntPtr.Zero ? GetMonitorForWindow(bestHwnd) : null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] GetMonitorForProcess({processName}) failed: {ex.Message}");
            return null;
        }
    }

    public MonitorInfo? GetMonitorForWindow(IntPtr hWnd)
    {
        if (hWnd == IntPtr.Zero) return null;

        try
        {
            if (!GetWindowRect(hWnd, out var rect)) return null;

            IntPtr hMonitor = MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST);
            if (hMonitor == IntPtr.Zero) return null;

            foreach (var native in _enumerateMonitors())
            {
                if (native.Handle == hMonitor) return BuildMonitorInfo(native);
            }
            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] GetMonitorForWindow failed: {ex.Message}");
            return null;
        }
    }

    public MonitorInfo GetTargetMonitor(string? processName)
    {
        // 1. Monitor des Spielfensters (sofern zuordenbar)
        var viaWindow = GetMonitorForProcess(processName ?? "");
        if (viaWindow is not null) return viaWindow;

        // 2. sonst der primäre Monitor – niemals zufällig
        var primary = GetPrimaryMonitor();
        if (primary is not null) return primary;

        // 3. Nichts ermittelbar → ehrlich "Unbekannt" statt Rateversuch
        return new MonitorInfo { DisplayName = "", IsAvailable = false };
    }

    // --- Intern: nativer Monitoring-Zugriff + Aufbereitung ----------------

    private MonitorInfo BuildMonitorInfo(NativeMonitor native)
    {
        bool primary = native.IsPrimary ||
            (native.Bounds.Left == 0 && native.Bounds.Top == 0 &&
             native.Bounds.Right > 0 && native.Bounds.Bottom > 0);

        var mode = _readCurrentDisplayMode(native.DeviceName);
        bool available = mode is { RefreshHz: > 0 } &&
                         mode.Value.RefreshHz <= 1000; // realistische Obergrenze

        return new MonitorInfo
        {
            DisplayName = native.DeviceName,
            RefreshRateHz = available ? mode!.Value.RefreshHz : 0,
            IsAvailable = available,
            MonitorId = native.MonitorDeviceId,
            IsPrimary = primary
        };
    }

    // --- Native Standard-Implementierungen --------------------------------

    private static IReadOnlyList<NativeMonitor> EnumerateMonitorsNative()
    {
        var list = new List<NativeMonitor>();

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT rect, IntPtr _) =>
        {
            var info = new MONITORINFOEXW();
            info.cbSize = Marshal.SizeOf<MONITORINFOEXW>();
            if (!GetMonitorInfoW(hMonitor, ref info)) return true;

            // Monitor-Geräte-ID (\\.\DISPLAY1\Monitor0) über EnumDisplayDevices
            string monitorId = info.szDevice;
            try
            {
                var dd = new DISPLAY_DEVICEW();
                dd.cb = Marshal.SizeOf<DISPLAY_DEVICEW>();
                if (EnumDisplayDevicesW(info.szDevice, 0, ref dd, 0) &&
                    !string.IsNullOrWhiteSpace(dd.DeviceName))
                {
                    monitorId = dd.DeviceName;
                }
            }
            catch { }

            list.Add(new NativeMonitor(
                hMonitor,
                info.szDevice,
                monitorId,
                (info.dwFlags & 1) != 0, // MONITORINFOF_PRIMARY
                new MonitorBounds(rect.Left, rect.Top, rect.Right, rect.Bottom)));
            return true;
        }, IntPtr.Zero);

        return list;
    }

    private static (int Width, int Height, int RefreshHz)? ReadCurrentDisplayModeNative(string deviceName)
    {
        var dm = new DEVMODEW();
        dm.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
        dm.dmDriverExtra = 0;

        if (!EnumDisplaySettingsW(deviceName, ENUM_CURRENT_SETTINGS, ref dm))
            return null;

        return (checked((int)dm.dmPelsWidth), checked((int)dm.dmPelsHeight), checked((int)dm.dmDisplayFrequency));
    }
}
