using System;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// NVIDIA-Treiber-FPS-Limit (Frame Rate Limiter / „Max Frame Rate“, DRS).
///
/// Verifizierte Bausteine (öffentlich belegte Quellen, direkt gelesen):
/// - `nvapi64.dll` + Export `nvapi_QueryInterface` (dokumentierter NVAPI-Einstieg,
///   bestätigt u. a. im öffentlichen 3Dmigoto-Quellcode)
/// - `NvAPI_Initialize`-Funktions-ID `0x0150E828` (community-verifiziert)
/// - `NvAPI_DRS_GetCurrentGlobalProfile`-Funktions-ID `0x617BFF9F` (community-verifiziert)
///
/// NICHT verifiziert (daher KEIN Auslesen, kein Raten — Spec: „NICHT raten“):
/// - Funktions-IDs für `NvAPI_DRS_GetSettings` / `NvAPI_DRS_GetSetting`
/// - Struct-Layouts + Versionen von `NVDRS_SETTINGS` / `NVDRS_SETTING`
/// - Setting-ID des Frame Rate Limiters (`NVDRS_FRAME_RATE_LIMITER`) aus dem
///   offiziellen SDK-Header `NvApiDriverSettings.h`
///
/// Konsequenz: Der Provider prüft NUR die verifizierten Einstiegspunkte
/// (nvapi64.dll vorhanden? nvapi_QueryInterface exportiert?) und liefert das
/// eigentliche Limit ehrlich als <see cref="LimiterStatus.Unknown"/> — ein
/// NVIDIA-System bedeutet NICHT, dass ein NVIDIA-FPS-Limit bekannt ist.
/// </summary>
public class NvidiaDriverLimitProvider : IDriverLimitProvider
{
    private readonly Func<IntPtr> _queryInterfaceLoader;

    public NvidiaDriverLimitProvider()
        : this(LoadNvApiQueryInterface)
    {
    }

    /// <summary>Test-Konstruktor mit injizierbarem Loader (IntPtr.Zero = API nicht verfügbar).</summary>
    public NvidiaDriverLimitProvider(Func<IntPtr> queryInterfaceLoader)
    {
        _queryInterfaceLoader = queryInterfaceLoader;
    }

    public LimiterSource Source => LimiterSource.Nvidia;

    public LimiterState GetLimitForProcess(string? processName)
    {
        try
        {
            IntPtr queryInterface = _queryInterfaceLoader();
            if (queryInterface == IntPtr.Zero)
            {
                // NVAPI nicht verfügbar → Limit nicht ermittelbar (nie 0, nie erfunden)
                return LimiterState.Unknown(LimiterSource.Nvidia);
            }

            // NVAPI ist vorhanden, aber die DRS-Lese-Kette (Setting-ID + Struct-Layouts)
            // ist nicht aus dem öffentlich verifizierbaren SDK belegt → ehrlich Unknown.
            return LimiterState.Unknown(LimiterSource.Nvidia);
        }
        catch
        {
            // Jeder Fehler → Unknown; kein Crash, kein Raten.
            return LimiterState.Unknown(LimiterSource.Nvidia);
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