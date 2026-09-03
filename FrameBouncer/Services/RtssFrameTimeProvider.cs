using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameBouncer.Models;

namespace FrameBouncer.Services;

/// <summary>
/// Echte RTSS-Datenquelle (RTSSSharedMemoryV2, gleiche Konvention wie RtssService):
/// - dwFrameTime (Offset 280) = Frametime in Mikrosekunden → gemessen (Source=Measured)
/// - Fallback Frames/(Time1−Time0) → abgeleitet (Source=Derived)
/// - FT280=0 ohne Fallback = nicht verfügbar (Source=Unavailable) → wird NIEMALS erfunden.
/// Kurzer "Hold" des letzten Werts (max. 4 Ticks ~100 ms) überbrückt Messlücken,
/// ohne Fake-Daten zu erzeugen.
/// </summary>
public class RtssFrameTimeProvider : IFrameTimeProvider
{
    private const uint RtssSignature = 0x52545353;

    private static readonly string[] SharedMemoryNames =
    [
        "RTSSSharedMemoryV2",
        "Local\\RTSSSharedMemoryV2",
        "Global\\RTSSSharedMemoryV2"
    ];

    private double _lastFrameTimeMs;
    private int _consecutiveZeroReads;
    private string? _lastProcessName;

    public FrameTimeSample GetNextSample(int targetFps)
    {
        double targetMs = 1000.0 / Math.Max(1, targetFps);
        uint focusPid = GetForegroundProcessId();

        double frameTimeMs = 0;
        double fps = 0;
        FrameTimeSource source = FrameTimeSource.Unavailable;
        string processName = "";

        var result = TryReadMemorySample(focusPid);
        if (result.fps > 0)
        {
            frameTimeMs = result.frameTimeMs;
            fps = result.fps;
            source = result.source;
            processName = result.processName;
            _consecutiveZeroReads = 0;
            _lastFrameTimeMs = frameTimeMs;
            _lastProcessName = processName;
        }

        if (source == FrameTimeSource.Unavailable)
        {
            _consecutiveZeroReads++;

            // Kurzzeitiges Halten des letzten Werts (max 4 Ticks = ~100ms),
            // um Messlücken zu überbrücken. Der gehaltene Wert ist MESSED.
            if (_consecutiveZeroReads < 5 && _lastFrameTimeMs > 0)
            {
                frameTimeMs = _lastFrameTimeMs;
                fps = 1000.0 / frameTimeMs;
                source = FrameTimeSource.Measured;
                processName = _lastProcessName ?? "";
            }
            else
            {
                // Keine Daten - ehrlich "nicht verfügbar" melden (keine Simulation!)
                return new FrameTimeSample
                {
                    Timestamp = DateTime.UtcNow,
                    FrameTimeMs = 0,
                    Fps = 0,
                    IsSpike = false,
                    TargetFrameTimeMs = Math.Round(targetMs, 2),
                    Source = FrameTimeSource.Unavailable,
                    ProcessName = ""
                };
            }
        }

        return new FrameTimeSample
        {
            Timestamp = DateTime.UtcNow,
            FrameTimeMs = Math.Round(frameTimeMs, 2),
            Fps = Math.Round(fps, 1),
            IsSpike = frameTimeMs > targetMs * 1.45,
            TargetFrameTimeMs = Math.Round(targetMs, 2),
            Source = source,
            ProcessName = processName
        };
    }

    /// <summary>
    /// Liest ein gültiges Sample aus dem RTSS-Shared-Memory. Als virtuelle Methode
    /// ausgelegt, damit Tests eine deterministisch leere Datenquelle erzwingen können
    /// (die Umgebung des Testrechners darf das Ergebnis nicht beeinflussen).
    /// </summary>
    protected virtual (double fps, double frameTimeMs, FrameTimeSource source, string processName) TryReadMemorySample(uint focusPid)
    {
        foreach (string memName in SharedMemoryNames)
        {
            var result = TryReadFpsData(memName, focusPid);
            if (result.fps > 0) return result;
        }

        return (0, 0, FrameTimeSource.Unavailable, "");
    }

    private static uint GetForegroundProcessId()
    {
        try
        {
            IntPtr hWnd = NativeMethods.GetForegroundWindow();
            if (hWnd != IntPtr.Zero)
            {
                NativeMethods.GetWindowThreadProcessId(hWnd, out uint pid);
                return pid;
            }
        }
        catch { }
        return 0;
    }

    private (double fps, double frameTimeMs, FrameTimeSource source, string processName) TryReadFpsData(
        string memoryName, uint focusPid)
    {
        IntPtr hMap = IntPtr.Zero;
        IntPtr pMem = IntPtr.Zero;

        try
        {
            hMap = NativeMethods.OpenFileMappingA(NativeMethods.FILE_MAP_READ, false, memoryName);
            if (hMap == IntPtr.Zero) return (0, 0, FrameTimeSource.Unavailable, "");

            pMem = NativeMethods.MapViewOfFile(hMap, NativeMethods.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (pMem == IntPtr.Zero) return (0, 0, FrameTimeSource.Unavailable, "");

            // Zentrale Signatur-/Versionsprüfung + Layout aus dem Header (Punkt 2)
            if (!RtssSharedMemoryHeader.TryRead(pMem, out var header)) return (0, 0, FrameTimeSource.Unavailable, "");

            uint appEntrySize = header.AppEntrySize;
            uint appArrOffset = header.AppArrOffset;
            uint appArrSize = header.AppArrSize;

            // Plausibilitätsgrenze aus der tatsächlich gemappten View statt fester 64 KB:
            // moderne RTSS-Versionen legen App-Entries bei Offsets von mehreren MB ab.
            long maxOffset = (long)NativeMethods.GetMappedRegionSize(pMem);
            if (maxOffset <= 0) maxOffset = 64L * 1024 * 1024; // Fallback-Sicherheitsgrenze

            // 1) Exakter Match auf den Fokus-PID (Vordergrund-Fenster)
            for (uint i = 0; i < appArrSize; i++)
            {
                long b = appArrOffset + (i * appEntrySize);
                if (b + 284 > maxOffset) break;

                int pid = Marshal.ReadInt32(pMem, (int)b);
                if (pid <= 0) continue;

                if (focusPid > 0 && (uint)pid == focusPid)
                {
                    string entryName = Marshal.PtrToStringAnsi(pMem + (int)b + 4, 260)?.TrimEnd('\0') ?? "";
                    var data = ReadEntryFrameData(pMem, b);
                    if (data.fps > 0)
                    {
                        return (data.fps, data.frameTimeMs, data.source, entryName);
                    }
                }
            }

            // 2) Fallback: bester gemessener Eintrag (höchste FPS)
            double bestFps = 0;
            double bestFt = 0;
            FrameTimeSource bestSource = FrameTimeSource.Unavailable;
            string bestName = "";

            for (uint i = 0; i < appArrSize; i++)
            {
                long b = appArrOffset + (i * appEntrySize);
                if (b + 284 > maxOffset) break;

                int pid = Marshal.ReadInt32(pMem, (int)b);
                if (pid <= 0) continue;

                string entryName = Marshal.PtrToStringAnsi(pMem + (int)b + 4, 260)?.TrimEnd('\0') ?? "";
                var data = ReadEntryFrameData(pMem, b);

                if (data.fps > bestFps)
                {
                    bestFps = data.fps;
                    bestFt = data.frameTimeMs;
                    bestSource = data.source;
                    bestName = entryName;
                }
            }

            return (bestFps, bestFt, bestSource, bestName);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RTSS Reader] Error: {ex.Message}");
            return (0, 0, FrameTimeSource.Unavailable, "");
        }
        finally
        {
            if (pMem != IntPtr.Zero) NativeMethods.UnmapViewOfFile(pMem);
            if (hMap != IntPtr.Zero) NativeMethods.CloseHandle(hMap);
        }
    }

    private static (double fps, double frameTimeMs, FrameTimeSource source) ReadEntryFrameData(IntPtr pMem, long b)
    {
        // Konvention zentral im getesteten Parser (siehe RtssFrameDataParser):
        // dwFrameTime in µs → FPS=1e6/FT; FT280=0 → Frames/Δt-Fallback; sonst unavailable.
        uint frameTimeUs = (uint)Marshal.ReadInt32(pMem, (int)b + 280);
        uint time0 = (uint)Marshal.ReadInt32(pMem, (int)b + 268);
        uint time1 = (uint)Marshal.ReadInt32(pMem, (int)b + 272);
        uint frames = (uint)Marshal.ReadInt32(pMem, (int)b + 276);

        return RtssFrameDataParser.Parse(frameTimeUs, time0, time1, frames);
    }
}
