using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace FrameBouncer.Services;

/// <summary>
/// RTSS-Anbindung. Zwei ergänzende Pfade:
///
/// 1. LIVE-PFAD: RTSSHooks64.dll (LoadProfile → SetProfileProperty → SaveProfile →
///    UpdateProfiles). Wirksam SOFORT für laufende/neue Prozesse, ohne RTSS-Neustart.
///    SaveProfile kann ohne Adminrechte false liefern (ACL-geschützte Profiles\-Datei) –
///    das ist OK, das persistente Schreiben übernimmt dann Pfad 2.
///    Kein LoadLibrary im UI-Thread-Pfad: DLL-Initialisierung in einer gehookten
///    Umgebung kann hängen – stattdessen Phase-Check über GetModuleHandle.
///
/// 2. PERSISTENZ-PFAD: Profiles\&lt;exe&gt;.cfg direkt (wenn Rechte ausreichen) oder
///    über den elevierten ElevationHelper (UAC on demand). Files in ProfileTemplates\
///    sind NUR GUI-Vorlagen und haben keinen Einfluss auf laufende Spiele.
/// </summary>
public class RtssService : IRtssService
{
    private const string SharedMemoryName = "RTSSSharedMemoryV2";

    private static readonly string[] SharedMemoryNames =
    [
        "RTSSSharedMemoryV2",
        "Local\\RTSSSharedMemoryV2",
        "Global\\RTSSSharedMemoryV2"
    ];

    private string? _rtssInstallPath;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void LoadProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SaveProfileDelegate([MarshalAs(UnmanagedType.LPStr)] string profile);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private delegate bool SetProfilePropertyDelegate(
        [MarshalAs(UnmanagedType.LPStr)] string propertyName,
        IntPtr propertyData,
        uint propertySize);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UpdateProfilesDelegate();

    private IntPtr _hHooksDll = IntPtr.Zero;
    private LoadProfileDelegate? _loadProfile;
    private SaveProfileDelegate? _saveProfile;
    private SetProfilePropertyDelegate? _setProfileProperty;
    private UpdateProfilesDelegate? _updateProfiles;

    public RtssService()
    {
        // RTSSHooks64.dll im Hintergrund vorladen (Regression "Apply hängt"):
        // Die DLL-Initialisierung kann in einer gehookten Umgebung hängen und darf
        // den UI-Thread NIE blockieren. Nach dem Preload genügt im Apply-Pfad ein
        // GetModuleHandle-Check – der Live-Pfad (sofort wirksam, ohne UAC) bleibt
        // trotzdem voll verfügbar.
        Task.Run(TryLoadHooksDllInBackground);
    }

    private void TryLoadHooksDllInBackground()
    {
        try
        {
            if (_hHooksDll != IntPtr.Zero) return;

            string? installPath = GetRtssInstallPath();
            if (string.IsNullOrEmpty(installPath)) return;

            string dllPath = Path.Combine(installPath, "RTSSHooks64.dll");
            if (!File.Exists(dllPath))
                dllPath = Path.Combine(installPath, "RTSSHooks.dll");
            if (!File.Exists(dllPath)) return;

            IntPtr h = NativeMethods.LoadLibrary(dllPath);
            if (h != IntPtr.Zero)
            {
                _hHooksDll = h;
                Debug.WriteLine("[FrameBouncer] RTSSHooks64.dll preloaded (background)");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] Hooks preload failed: {ex.Message}");
        }
    }

    public bool IsRtssAvailable()
    {
        foreach (string name in SharedMemoryNames)
        {
            try
            {
                IntPtr hMap = NativeMethods.OpenFileMappingA(NativeMethods.FILE_MAP_READ, false, name);
                if (hMap != IntPtr.Zero)
                {
                    NativeMethods.CloseHandle(hMap);
                    return true;
                }
            }
            catch { }
        }
        return false;
    }

    public double ReadFpsFromRtss(string processName)
    {
        foreach (string memName in SharedMemoryNames)
        {
            double result = TryReadFps(memName, processName);
            if (result > 0) return result;
        }
        return 0;
    }

    private double TryReadFps(string memoryName, string processName)
    {
        IntPtr hMap = IntPtr.Zero;
        IntPtr pMem = IntPtr.Zero;

        try
        {
            hMap = NativeMethods.OpenFileMappingA(NativeMethods.FILE_MAP_READ, false, memoryName);
            if (hMap == IntPtr.Zero) return 0;

            pMem = NativeMethods.MapViewOfFile(hMap, NativeMethods.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (pMem == IntPtr.Zero) return 0;

            // Zentrale Signatur-/Versionsprüfung + Layout aus dem Header
            if (!RtssSharedMemoryHeader.TryRead(pMem, out var header)) return 0;

            uint appEntrySize = header.AppEntrySize;
            uint appArrOffset = header.AppArrOffset;
            uint appArrSize = header.AppArrSize;

            // Plausibilitätsgrenze aus der tatsächlich gemappten View statt fester 64 KB:
            // moderne RTSS-Versionen legen App-Entries bei Offsets von mehreren MB ab.
            long maxOffset = (long)NativeMethods.GetMappedRegionSize(pMem);
            if (maxOffset <= 0) maxOffset = 64L * 1024 * 1024; // Fallback-Sicherheitsgrenze

            for (uint i = 0; i < appArrSize; i++)
            {
                long entryBase = appArrOffset + (i * appEntrySize);

                if (entryBase + 284 > maxOffset) break;

                int pid = Marshal.ReadInt32(pMem, (int)entryBase);
                if (pid <= 0) continue;

                string entryName = Marshal.PtrToStringAnsi(pMem + (int)entryBase + 4, 260)?.TrimEnd('\0') ?? "";

                string entryExe = Path.GetFileName(entryName);
                if (!entryName.Equals(processName, StringComparison.OrdinalIgnoreCase)
                    && !entryExe.Equals(processName, StringComparison.OrdinalIgnoreCase))
                    continue;

                uint frameTime = (uint)Marshal.ReadInt32(pMem, (int)entryBase + 280);

                if (frameTime > 0)
                    return Math.Round(1000000.0 / frameTime, 1);

                uint time0 = (uint)Marshal.ReadInt32(pMem, (int)entryBase + 268);
                uint time1 = (uint)Marshal.ReadInt32(pMem, (int)entryBase + 272);
                uint frames = (uint)Marshal.ReadInt32(pMem, (int)entryBase + 276);

                if (time1 > time0 && frames > 0)
                    return Math.Round(1000.0 * frames / (time1 - time0), 1);

                break;
            }
        }
        catch { }
        finally
        {
            if (pMem != IntPtr.Zero) NativeMethods.UnmapViewOfFile(pMem);
            if (hMap != IntPtr.Zero) NativeMethods.CloseHandle(hMap);
        }

        return 0;
    }

    public void SetFpsLimitViaRtss(string processName, int targetFps)
    {
        Debug.WriteLine($"[FrameBouncer] SetFpsLimitViaRtss: process={processName}, fps={targetFps}");

        // Pfad 1: LIVE über RTSSHooks-API (sofort wirksam, keine Rechte nötig)
        bool hooksLive = TrySetFpsViaHooks(processName, targetFps);

        // Pfad 2: Persistenz (Profiles\<exe>.cfg direkt oder elevierter Helper).
        // Richtet auch das Profil ein, wenn die DLL nicht geladen werden konnte.
        bool persisted = TryPersistProfile(processName, targetFps);

        Debug.WriteLine($"[FrameBouncer] Result: HooksLive={hooksLive}, Persisted={persisted}");

        // Laufende RTSS-Instanz über Profiländerungen informieren (nicht-blockierend)
        if (hooksLive || persisted)
        {
            NotifyRtssProfilesChanged();
        }
    }

    /// <summary>
    /// Wurde RTSSHooks64.dll bereits in diesen Prozess geladen (z. B. durch einen
    /// früheren Apply in derselben Session)? Kein LoadLibrary – das kann in einer
    /// gehookten Umgebung hängen und friert sonst den UI-Thread ein.
    /// </summary>
    private bool IsHooksDllLoaded()
    {
        if (_hHooksDll != IntPtr.Zero) return true;
        return NativeMethods.GetModuleHandle("RTSSHooks64.dll") != IntPtr.Zero;
    }

    private bool TrySetFpsViaHooks(string processName, int targetFps)
    {
        try
        {
            // DLL nur nutzen, wenn sie bereits geladen ist (kein LoadLibrary im UI-Thread;
            // das übernimmt der Hintergrund-Preload in TryLoadHooksDllInBackground).
            //
            // REGRESSION (Live-Pfad war tot): Der Preload setzt nur das Modul-Handle
            // (_hHooksDll). Ein reiner Handle-Check ließ InitHooksDllFromLoaded nie
            // laufen, sobald der Preload fertig war → alle Export-Delegates blieben
            // null und SetProfileProperty lieferte immer false. Deshalb hier IMMER
            // idempotent binden, sobald das Modul im Prozess ist.
            if (_setProfileProperty is null)
            {
                if (!IsHooksDllLoaded())
                {
                    Debug.WriteLine("[FrameBouncer] RTSSHooks64.dll noch nicht geladen – Live-Pfad übersprungen");
                    return false;
                }
                if (!InitHooksDllFromLoaded())
                {
                    Debug.WriteLine("[FrameBouncer] RTSSHooks64.dll geladen, aber Export-Bindung fehlgeschlagen");
                    return false;
                }
            }

            Debug.WriteLine($"[FrameBouncer] LoadProfile({processName})");
            _loadProfile?.Invoke(processName);

            IntPtr pLimit = Marshal.AllocHGlobal(sizeof(int));
            IntPtr pDenominator = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(pDenominator, 1);
                bool denomOk = _setProfileProperty?.Invoke("FramerateLimitDenominator", pDenominator, (uint)sizeof(int)) ?? false;
                Debug.WriteLine($"[FrameBouncer] SetProfileProperty(FramerateLimitDenominator, 1) = {denomOk}");

                Marshal.WriteInt32(pLimit, targetFps);
                bool setResult = _setProfileProperty?.Invoke("FramerateLimit", pLimit, (uint)sizeof(int)) ?? false;
                Debug.WriteLine($"[FrameBouncer] SetProfileProperty(FramerateLimit, {targetFps}) = {setResult}");

                if (!setResult)
                {
                    Debug.WriteLine("[FrameBouncer] Live-Limit NICHT gesetzt (SetProfileProperty=false)");
                    return false;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(pLimit);
                Marshal.FreeHGlobal(pDenominator);
            }

            // Persistenz aus der DLL kann ohne Adminrechte fehlschlagen (ACL) –
            // dann übernimmt TryPersistProfile das dauerhafte Schreiben.
            bool saved = _saveProfile?.Invoke(processName) ?? false;
            Debug.WriteLine($"[FrameBouncer] SaveProfile({processName}) = {saved} (false ist non-elevated normal)");

            _updateProfiles?.Invoke();
            Debug.WriteLine("[FrameBouncer] UpdateProfiles() called");

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] RTSSHooks error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Binded die bereits geladene RTSSHooks64.dll an unsere Delegates (GetProcAddress
    /// ist sicher und hängt nicht). Liefert false, wenn die DLL nicht geladen ist oder
    /// Exporte fehlen.
    /// </summary>
    private bool InitHooksDllFromLoaded()
    {
        try
        {
            IntPtr hHooksDll = NativeMethods.GetModuleHandle("RTSSHooks64.dll");
            if (hHooksDll == IntPtr.Zero) return false;

            IntPtr pLoadProfile = NativeMethods.GetProcAddress(hHooksDll, "LoadProfile");
            IntPtr pSaveProfile = NativeMethods.GetProcAddress(hHooksDll, "SaveProfile");
            IntPtr pSetProfileProperty = NativeMethods.GetProcAddress(hHooksDll, "SetProfileProperty");
            IntPtr pUpdateProfiles = NativeMethods.GetProcAddress(hHooksDll, "UpdateProfiles");

            if (pLoadProfile == IntPtr.Zero || pSaveProfile == IntPtr.Zero ||
                pSetProfileProperty == IntPtr.Zero || pUpdateProfiles == IntPtr.Zero)
            {
                Debug.WriteLine("[FrameBouncer] RTSSHooks64.dll geladen, aber Exporte fehlen");
                return false;
            }

            _hHooksDll = hHooksDll;
            _loadProfile = Marshal.GetDelegateForFunctionPointer<LoadProfileDelegate>(pLoadProfile);
            _saveProfile = Marshal.GetDelegateForFunctionPointer<SaveProfileDelegate>(pSaveProfile);
            _setProfileProperty = Marshal.GetDelegateForFunctionPointer<SetProfilePropertyDelegate>(pSetProfileProperty);
            _updateProfiles = Marshal.GetDelegateForFunctionPointer<UpdateProfilesDelegate>(pUpdateProfiles);

            Debug.WriteLine("[FrameBouncer] RTSSHooks64.dll (bereits geladen) gebunden");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] Bind RTSSHooks failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Persistiert das Limit in der aktiven RTSS-Profil-Datei: direkt, wenn die
    /// Rechte ausreichen, sonst über den elevierten ElevationHelper (UAC on demand,
    /// einmal pro Apply — kein UAC-Spam, keine Endlosschleife).
    /// </summary>
    private bool TryPersistProfile(string processName, int targetFps)
    {
        try
        {
            string? installPath = GetRtssInstallPath();
            if (string.IsNullOrEmpty(installPath))
            {
                Debug.WriteLine("[FrameBouncer] RTSS install path not found");
                return false;
            }

            // 1. Direkt schreiben – funktioniert, wenn die App ausreichend Rechte hat
            try
            {
                RtssProfileWriter.SetProfileLimit(installPath, processName, targetFps);
                Debug.WriteLine($"[FrameBouncer] Profiles profile written directly: Limit={targetFps}");
                return true;
            }
            catch (UnauthorizedAccessException)
            {
                Debug.WriteLine("[FrameBouncer] Direct write to Profiles denied - using elevated helper");
            }
            catch (IOException ex)
            {
                Debug.WriteLine($"[FrameBouncer] Direct write to Profiles failed: {ex.Message}");
            }

            // 2. Elevierter Helper: schreibt das Profil mit Adminrechten.
            //    Fordert ON DEMAND eine UAC-Freigabe an (nicht beim App-Start).
            return TrySetFpsViaElevatedHelper(installPath, processName, targetFps);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] Profile persist error: {ex.Message}");
            return false;
        }
    }

    private bool TrySetFpsViaElevatedHelper(string installPath, string processName, int targetFps)
    {
        try
        {
            string? helperPath = FindElevationHelper();
            if (string.IsNullOrEmpty(helperPath))
            {
                Debug.WriteLine("[FrameBouncer] ElevationHelper not found");
                return false;
            }

            using var proc = Process.Start(new ProcessStartInfo
            {
                FileName = helperPath,
                Arguments = $"writeLimit \"{installPath}\" \"{processName}\" {targetFps}",
                UseShellExecute = true,
                Verb = "runas" // explizite, benutzerbestätigte Elevation
            });

            if (proc is null) return false;

            if (!proc.WaitForExit(15_000))
            {
                Debug.WriteLine("[FrameBouncer] ElevationHelper timeout");
                return false;
            }

            Debug.WriteLine($"[FrameBouncer] ElevationHelper exit code: {proc.ExitCode}");
            return proc.ExitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // Benutzer hat die UAC-Anfrage abgelehnt
            Debug.WriteLine("[FrameBouncer] Elevation cancelled by user");
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] ElevationHelper error: {ex.Message}");
            return false;
        }
    }

    private static string? FindElevationHelper()
    {
        // 1. Neben der eigenen EXE (typisch für dotnet publish / Release-Layout)
        string besideExe = Path.Combine(AppContext.BaseDirectory, "FrameBouncer.ElevationHelper.exe");
        if (File.Exists(besideExe)) return besideExe;

        // 2. Layout von dotnet build: ../FrameBouncer.ElevationHelper/bin/Debug/net8.0-windows/
        string buildLayout = Path.Combine(AppContext.BaseDirectory, "..", "..", "..",
            "..", "FrameBouncer.ElevationHelper", "bin", "Debug", "net8.0-windows",
            "FrameBouncer.ElevationHelper.exe");
        string full = Path.GetFullPath(buildLayout);
        if (File.Exists(full)) return full;

        return null;
    }

    private string? GetRtssInstallPath()
    {
        if (!string.IsNullOrEmpty(_rtssInstallPath) && Directory.Exists(_rtssInstallPath))
            return _rtssInstallPath;

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"Software\WOW6432Node\Unwinder\RTSS");
            if (key?.GetValue("InstallDir") is string value && Directory.Exists(value))
            {
                _rtssInstallPath = value;
                return value;
            }
        }
        catch { }

        string defaultPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "RivaTuner Statistics Server");

        if (Directory.Exists(defaultPath))
        {
            _rtssInstallPath = defaultPath;
            return defaultPath;
        }

        return null;
    }

    private void NotifyRtssProfilesChanged()
    {
        try
        {
            IntPtr hWnd = FindRtssWindow();
            if (hWnd != IntPtr.Zero)
            {
                const uint WM_APP = 0x8000;

                // RTSS API: WM_APP + 0x100 = Profile neu laden (PostMessage blockiert nicht)
                NativeMethods.PostMessage(hWnd, WM_APP + 0x100, IntPtr.Zero, IntPtr.Zero);
                Debug.WriteLine("[FrameBouncer] Sent WM_APP+0x100 (reload profiles) to RTSS");
            }
            else
            {
                Debug.WriteLine("[FrameBouncer] RTSS window not found - profile written but may need RTSS restart");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[FrameBouncer] NotifyRtss error: {ex.Message}");
        }
    }

    private static IntPtr FindRtssWindow()
    {
        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((hWnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hWnd)) return true;
            var sb = new StringBuilder(256);
            NativeMethods.GetWindowText(hWnd, sb, 256);
            string title = sb.ToString();
            if (title.Contains("RivaTuner", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("RTSS", StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
