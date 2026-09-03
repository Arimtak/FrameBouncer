using System;
using System.Runtime.InteropServices;

namespace FrameBouncer.Services;

/// <summary>
/// Liest und validiert den Header des RTSS-Shared-Memoryblocks (RTSSSharedMemoryV2).
/// Zentrale Stelle für Signatur- und Versionsprüfung (Kompabilitäts-Spec Punkt 2),
/// damit beide Reader (RtssService + RtssFrameTimeProvider) exakt dieselbe Prüfung
/// nutzen. Die Signatur wird strikt geprüft; das Layout (Entry-Größe/-Offset/-Anzahl)
/// kommt aus dem Header selbst und wird nicht blind als fixe Zahl angenommen.
/// </summary>
public static class RtssSharedMemoryHeader
{
    /// <summary>RTSS-Shared-Memory-Signatur ("RTSS").</summary>
    public const uint RtssSignature = 0x52545353;

    /// <summary>
    /// Minimale Größe eines App-Entrys des V2-Layouts. Dient als Plausibilitätsgrenze,
    /// damit ein korrupter/unerwarteter Layout-Wert nicht zu wilden Offsets führt.
    /// </summary>
    public const uint MinAppEntrySize = 284;

    /// <summary>
    /// Gelesenes Header-/Layout-Ergebnis. Version == 0 bedeutet "von RTSS nicht gesetzt" –
    /// wird als unbekannt, aber nicht als Fehler behandelt (Signatur ist der Anker).
    /// </summary>
    public readonly record struct Info(
        uint Signature,
        uint Version,
        uint AppEntrySize,
        uint AppArrOffset,
        uint AppArrSize);

    /// <summary>
    /// Prüft die Signatur und liest die Layout-/Versionsfelder. Gibt false zurück bei
    /// Null-Pointer, falscher Signatur oder unbrauchbarem Layout (Entry-Größe/-Anzahl == 0
    /// oder Entry-Größe unter der Plausibilitätsgrenze). Es wird bewusst NICHT auf eine
    /// exakte RTSS-Versionsnummer gegated: Eine gültige, nur unbekannte Version soll nicht
    /// fälschlich als "nicht unterstützt" abgelehnt werden. Fehler führen nie zu einem Crash.
    /// </summary>
    public static bool TryRead(IntPtr pMem, out Info info)
    {
        info = default;
        try
        {
            if (pMem == IntPtr.Zero) return false;

            uint signature = (uint)Marshal.ReadInt32(pMem, 0);
            if (signature != RtssSignature) return false;

            uint version = (uint)Marshal.ReadInt32(pMem, 4);
            uint appEntrySize = (uint)Marshal.ReadInt32(pMem, 8);
            uint appArrOffset = (uint)Marshal.ReadInt32(pMem, 12);
            uint appArrSize = (uint)Marshal.ReadInt32(pMem, 16);

            if (appEntrySize < MinAppEntrySize || appArrSize == 0) return false;

            info = new Info(signature, version, appEntrySize, appArrOffset, appArrSize);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
