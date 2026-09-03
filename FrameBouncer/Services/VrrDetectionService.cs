using System;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FrameBouncer.Services;

/// <summary>
/// VRR-Erkennung über die VESA-EDID Range Limits des Zielmonitors.
///
/// Datenquelle (verifizierbar, dokumentiert):
/// 1. Monitor → Geräte-ID über EnumDisplayDevicesW (dokumentierte Win32-API)
/// 2. EDID-Bytes über SetupAPI (GUID_DEVCLASS_MONITOR) + Registry-Wert
///    „EDID“ unter HKLM\...\Enum\DISPLAY\&lt;instance&gt;\Device Parameters
/// 3. Range Limits aus dem EDID-Basisblock (VESA-Standard, siehe
///    EdidRangeLimitsParser) + konservativer Support-Heuristik.
///
/// Ehrlichkeits-Grenzen (Spec Punkte 2/3/13): Aktiver VRR-Status und
/// Technologie (G-SYNC/FreeSync/Adaptive Sync) sind über keine öffentlich
/// dokumentierte Windows-API auslesbar → sie bleiben "Unknown". Aus
/// GPU-Hersteller oder Monitorname wird niemals geschlossen.
///
/// Testbarkeit: EDID-Leser und Support-Bewertung sind injizierbare Fabriken.
/// </summary>
public class VrrDetectionService : IVrrDetectionService
{
    private const uint DIGCF_PRESENT = 0x00000002;
    private static readonly Guid GuidDeviceClassMonitor = new("4d36e96e-e325-11ce-bfc1-08002be10318");

    // --- Win32-P/Invoke -------------------------------------------------

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

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string lpDevice, uint iDevNum, ref DISPLAY_DEVICEW lpDisplayDevice, uint dwFlags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SP_DEVINFO_DATA deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr deviceInfoSet, ref SP_DEVINFO_DATA deviceInfoData, StringBuilder deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll")]
    private static extern void SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    // --- Testbare Fabriken ----------------------------------------------

    private readonly Func<MonitorInfo, byte[]?> _readEdid;
    private readonly Func<EdidRangeLimits?, VrrSupport> _evaluateSupport;

    public VrrDetectionService()
        : this(ReadEdidNative, EvaluateSupportDefault)
    {
    }

    /// <summary>Test-Konstruktor mit injiziertem EDID-Leser und Support-Bewertung.</summary>
    public VrrDetectionService(
        Func<MonitorInfo, byte[]?> readEdid,
        Func<EdidRangeLimits?, VrrSupport>? evaluateSupport = null)
    {
        _readEdid = readEdid;
        _evaluateSupport = evaluateSupport ?? EvaluateSupportDefault;
    }

    // --- Interface ------------------------------------------------------

    public MonitorInfo Detect(MonitorInfo monitor)
    {
        // Kein gültiger Monitor → VRR nicht verfügbar (ehrlich, kein Rateversuch)
        if (!monitor.IsAvailable ||
            (string.IsNullOrWhiteSpace(monitor.MonitorId) && string.IsNullOrWhiteSpace(monitor.DisplayName)))
        {
            return monitor.WithVrr(VrrSupport.Unavailable, VrrState.Unavailable, VrrTechnology.None);
        }

        byte[]? edid = null;
        try
        {
            edid = _readEdid(monitor);
        }
        catch
        {
            // Lesefehler → ehrlich Unknown statt erfundener Werte
        }

        var limits = EdidRangeLimitsParser.TryParse(edid);
        var support = _evaluateSupport(limits);

        // Aktiver Status & Technologie: keine öffentlich verifizierbare Windows-API
        // (Spec Punkte 2/13) → ehrlich Unknown, niemals geraten.
        return monitor.WithVrr(support, VrrState.Unknown, VrrTechnology.Unknown);
    }

    // --- Support-Bewertung (dokumentierter, konservativer Heuristik-Check) ----

    /// <summary>
    /// Bewertet VRR-Unterstützung aus den EDID Range Limits:
    /// - Supported:  min ≤ 48 Hz, max ≥ 90 Hz, Spanne ≥ 40 Hz (typische
    ///   FreeSync-/G-SYNC-Bereiche, z. B. 40–144, 48–144, 30–144).
    /// - NotSupported: schmale, hohe Spanne (min ≥ 50, max−min &lt; 25) –
    ///   der Monitor deklariert selbst keine variablen Raten (z. B. 56–60).
    /// - Unknown: alles andere (z. B. 60–120) – nicht zuverlässig entscheidbar.
    /// Konservativ: kaputte/minimale Angaben führen nie zu "Supported".
    /// </summary>
    public static VrrSupport EvaluateSupportDefault(EdidRangeLimits? limits)
    {
        if (limits is not { } l) return VrrSupport.Unknown;

        bool plausibleMin = l.MinVerticalHz is >= 20 and <= 48;
        bool wideSpan = l.MaxVerticalHz >= 90 && (l.MaxVerticalHz - l.MinVerticalHz) >= 40;
        if (plausibleMin && wideSpan) return VrrSupport.Supported;

        bool narrowFixed = l.MinVerticalHz >= 50 && (l.MaxVerticalHz - l.MinVerticalHz) < 25;
        if (narrowFixed) return VrrSupport.NotSupported;

        return VrrSupport.Unknown;
    }

    // --- Nativer EDID-Leser (SetupAPI + Registry) -------------------------

    /// <summary>
    /// Liest den EDID-Basisblock des Monitors. Ablauf: Geräte-ID über
    /// EnumDisplayDevicesW, Instanz-ID über SetupAPI (Modell-Token-Abgleich),
    /// EDID-Bytes aus der Registry. Liefert null, wenn nichts zuverlässig
    /// lesbar ist – niemals Teilwerte.
    /// </summary>
    private static byte[]? ReadEdidNative(MonitorInfo monitor)
    {
        try
        {
            string monitorDevice = !string.IsNullOrWhiteSpace(monitor.MonitorId)
                ? monitor.MonitorId
                : monitor.DisplayName;
            if (string.IsNullOrWhiteSpace(monitorDevice)) return null;

            // 1. Geräte-ID des Monitors (z. B. "MONITOR\DEL42BA\{GUID}\0000")
            var dd = new DISPLAY_DEVICEW();
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICEW>();
            if (!EnumDisplayDevicesW(monitorDevice, 0, ref dd, 0)) return null;

            string? modelToken = ExtractModelToken(dd.DeviceID);
            if (modelToken is null) return null;

            // 2. SetupAPI: Monitor-Geräteklasse durchgehen und Token abgleichen
            var classGuid = GuidDeviceClassMonitor;
            IntPtr devInfo = SetupDiGetClassDevsW(ref classGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
            if (devInfo == IntPtr.Zero || devInfo == (IntPtr)(-1)) return null;

            try
            {
                for (uint i = 0; ; i++)
                {
                    var devData = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
                    if (!SetupDiEnumDeviceInfo(devInfo, i, ref devData)) break;

                    if (!TryGetInstanceId(devInfo, ref devData, out string instanceId)) continue;

                    var parts = instanceId.Split('\\');
                    if (parts.Length < 2) continue;
                    if (!parts[1].Equals(modelToken, StringComparison.OrdinalIgnoreCase)) continue;

                    // 3. EDID aus der Registry lesen (dokumentierter Speicherort)
                    byte[]? edid = ReadEdidFromRegistry(instanceId);
                    if (edid is { Length: >= 128 }) return edid;
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(devInfo);
            }
        }
        catch
        {
            // Jeder Fehler → null → ehrlich "Unknown"
        }

        return null;
    }

    /// <summary>Modell-Token aus einer Geräte-ID (2. Segment, z. B. "DEL42BA").</summary>
    private static string? ExtractModelToken(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return null;
        var parts = deviceId.Split('\\');
        if (parts.Length < 2 || parts[1].Length == 0) return null;
        return parts[1];
    }

    private static bool TryGetInstanceId(IntPtr devInfo, ref SP_DEVINFO_DATA devData, out string instanceId)
    {
        instanceId = string.Empty;
        const int initialSize = 256;
        var buffer = new StringBuilder(initialSize);
        if (SetupDiGetDeviceInstanceIdW(devInfo, ref devData, buffer, (uint)buffer.Capacity, out uint required))
        {
            instanceId = buffer.ToString();
            return instanceId.Length > 0;
        }

        // Puffer zu klein → mit korrekter Größe erneut versuchen
        if (required <= initialSize || required > 1024) return false;
        var retry = new StringBuilder((int)required);
        if (!SetupDiGetDeviceInstanceIdW(devInfo, ref devData, retry, required, out _)) return false;
        instanceId = retry.ToString();
        return instanceId.Length > 0;
    }

    private static byte[]? ReadEdidFromRegistry(string instanceId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Enum\{instanceId}\Device Parameters");
            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            return null;
        }
    }
}