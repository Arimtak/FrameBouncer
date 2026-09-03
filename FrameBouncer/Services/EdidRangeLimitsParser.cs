namespace FrameBouncer.Services;

/// <summary>
/// Vertikale Eingangsfrequenz-Spanne eines Monitors laut VESA-EDID
/// „Display Range Limits“-Deskriptor.
/// </summary>
public readonly record struct EdidRangeLimits(byte MinVerticalHz, byte MaxVerticalHz);

/// <summary>
/// Reiner Parser für den VESA-EDID-Standard (dokumentierte Offsets):
/// Der 128-Byte-Basisblock enthält vier Detailed Timing Descriptors bei
/// Offset 54/72/90/108. Ein Deskriptor mit Pixel-Takt 0 (Bytes 0–2) und
/// Typ 0xFD ist der „Display Range Limits“-Deskriptor:
///   +4 = minimale vertikale Rate (Hz), +5 = maximale vertikale Rate (Hz).
/// Validierung: Header-Signatur (00 FF FF FF FF FF FF 00) + Prüfsumme
/// (Summe aller 128 Bytes ≡ 0 mod 256, Byte 127). Ungültige Daten → null.
/// </summary>
public static class EdidRangeLimitsParser
{
    private static readonly byte[] EdidHeader = { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 };

    private const int BaseBlockSize = 128;
    private const int FirstDtdOffset = 54;
    private const int DtdStride = 18;
    private const int LastDtdOffset = 108;
    private const byte RangeLimitsTag = 0xFD;

    /// <summary>
    /// Liest die vertikale Frequenz-Spanne aus dem EDID-Basisblock.
    /// Liefert null bei ungültigen Daten (kein Raten auf kaputten Input).
    /// </summary>
    public static EdidRangeLimits? TryParse(byte[]? edid)
    {
        if (edid is null || edid.Length < BaseBlockSize) return null;

        for (int i = 0; i < EdidHeader.Length; i++)
        {
            if (edid[i] != EdidHeader[i]) return null;
        }

        // Prüfsumme (VESA): Summe der 128 Bytes ≡ 0 (mod 256)
        uint sum = 0;
        for (int i = 0; i < BaseBlockSize; i++) sum += edid[i];
        if ((sum & 0xFF) != 0) return null;

        // Alle vier DTD-Slots nach dem Range-Limits-Deskriptor durchsuchen
        for (int offset = FirstDtdOffset; offset <= LastDtdOffset; offset += DtdStride)
        {
            if (edid[offset] != 0x00 || edid[offset + 1] != 0x00 || edid[offset + 2] != 0x00) continue;
            if (edid[offset + 3] != RangeLimitsTag) continue;

            byte minV = edid[offset + 4];
            byte maxV = edid[offset + 5];
            if (maxV == 0) continue; // kaputte Angabe → nächsten Slot prüfen

            return new EdidRangeLimits(minV, maxV);
        }

        return null;
    }
}