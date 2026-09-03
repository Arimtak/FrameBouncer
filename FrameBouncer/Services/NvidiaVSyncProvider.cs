using System;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// NVIDIA-Treiber-V-Sync (DRS-Setting <c>NVDRS_VSYNCMODE</c>).
///
/// Verifizierte Bausteine (öffentlich belegte Quellen, direkt gelesen):
/// - `nvapi64.dll` + Export `nvapi_QueryInterface` (dokumentierter NVAPI-Einstieg)
/// - `NvAPI_Initialize`-Funktions-ID `0x0150E828` (community-verifiziert)
///
/// NICHT verifiziert (daher KEIN Auslesen, kein Raten — Spec: „KEINE geratenen
/// Funktions-IDs“):
/// - Funktions-IDs für `NvAPI_DRS_GetSettings` / `NvAPI_DRS_GetSetting`
/// - Struct-Layouts + Versionen von `NVDRS_SETTINGS` / `NVDRS_SETTING`
/// - Setting-ID des V-Sync-Modus (`NVDRS_VSYNCMODE`) aus dem offiziellen
///   SDK-Header `NvApiDriverSettings.h`
///
/// Konsequenz: Der Provider prüft NUR die verifizierten Einstiegspunkte.
/// Fehlt nvapi64.dll → <see cref="LimiterStatus.Unavailable"/> (API nicht
/// vorhanden); ist die DLL geladen, aber der Setting-Wert nicht belegt →
/// ehrlich <see cref="LimiterStatus.Unknown"/>. „NVIDIA-Treiber vorhanden“
/// bedeutet NICHT „NVIDIA V-Sync bekannt“.
/// </summary>
public class NvidiaVSyncProvider : IVSyncProvider
{
    private readonly Func<IntPtr> _queryInterfaceLoader;

    public NvidiaVSyncProvider()
        : this(LoadNvApiQueryInterface)
    {
    }

    /// <summary>Test-Konstruktor mit injizierbarem Loader (IntPtr.Zero = API nicht verfügbar).</summary>
    public NvidiaVSyncProvider(Func<IntPtr> queryInterfaceLoader)
    {
        _queryInterfaceLoader = queryInterfaceLoader;
    }

    public LimiterSource Source => LimiterSource.NvidiaVSync;

    public LimiterState GetVSyncStateForProcess(string? processName)
    {
        try
        {
            IntPtr queryInterface = _queryInterfaceLoader();
            if (queryInterface == IntPtr.Zero)
            {
                // NVAPI fehlt → Quelle nicht verfügbar (nie 0, nie erfunden, nie "Aus").
                return LimiterState.Unavailable(LimiterSource.NvidiaVSync);
            }

            // NVAPI ist vorhanden, aber die DRS-Lese-Kette (Setting-ID + Struct-Layouts)
            // ist nicht aus dem öffentlich verifizierbaren SDK belegt → ehrlich Unknown.
            return LimiterState.Unknown(LimiterSource.NvidiaVSync);
        }
        catch
        {
            // Jeder Fehler → Unknown; kein Crash, kein Raten.
            return LimiterState.Unknown(LimiterSource.NvidiaVSync);
        }
    }

    /// <summary>
    /// Lädt nvapi64.dll und löst `nvapi_QueryInterface` auf — die dokumentierten,
    /// verifizierten NVAPI-Einstiegspunkte. Es werden KEINE Funktionen mit nicht
    /// verifizierten IDs aufgerufen (rein lesend, keine Seiteneffekte).
    /// </summary>
    private static IntPtr LoadNvApiQueryInterface()
    {
        try
        {
            IntPtr handle = NativeMethods.LoadLibrary("nvapi64.dll");
            if (handle == IntPtr.Zero) return IntPtr.Zero;
            return NativeMethods.GetProcAddress(handle, "nvapi_QueryInterface");
        }
        catch
        {
            return IntPtr.Zero;
        }
    }
}