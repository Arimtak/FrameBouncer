using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameBouncer.Services;

public class AfterburnerService : IAfterburnerService
{
    private const uint ExpectedSignature = 0x4D41484D; // "MAHM"
    private const int EntryNameOffset = 0;
    private const int EntryNameLength = 260;
    private const int EntryValueOffset = 1300;

    private static readonly string[] MemoryMapNames =
    [
        "MAHMSharedMemory",
        "Local\\MAHMSharedMemory",
        "Global\\MAHMSharedMemory"
    ];

    public bool IsAfterburnerAvailable()
    {
        foreach (string name in MemoryMapNames)
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                return true;
            }
            catch { }
        }
        return false;
    }

    public int? GetGpuTemperatureFromAfterburner()
    {
        return ReadSensorValue("GPU temperature");
    }

    public int? GetCpuTemperatureFromAfterburner()
    {
        return ReadSensorValue("CPU temperature");
    }

    private int? ReadSensorValue(string sensorName)
    {
        foreach (string mapName in MemoryMapNames)
        {
            try
            {
                using var mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
                using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);

                accessor.Read(0, out MahmHeader header);
                if (header.Signature != ExpectedSignature) continue;
                if (header.EntryCount == 0 || header.EntrySize == 0) continue;

                long currentOffset = header.HeaderSize;
                var entryBuffer = new byte[header.EntrySize];

                for (int i = 0; i < (int)header.EntryCount; i++)
                {
                    if (currentOffset + header.EntrySize > accessor.Capacity) break;

                    accessor.ReadArray(currentOffset, entryBuffer, 0, entryBuffer.Length);

                    string name = Encoding.ASCII.GetString(entryBuffer, EntryNameOffset, EntryNameLength)
                        .TrimEnd('\0').Trim();

                    if (name.Equals(sensorName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Sensor vorhanden: echten Messwert zurückgeben (auch wenn er z.B.
                        // technisch um 0°C liegt). Fehlender Sensor => null (unten).
                        float value = BitConverter.ToSingle(entryBuffer, EntryValueOffset);
                        return (int)Math.Round(value);
                    }

                    currentOffset += header.EntrySize;
                }
            }
            catch (FileNotFoundException)
            {
                // Nächsten Namen versuchen
            }
            catch
            {
                break;
            }
        }
        // Kein Sensor gefunden / Afterburner nicht verfügbar → ehrlich "nicht verfügbar"
        return null;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct MahmHeader
    {
        public uint Signature;
        public uint Version;
        public uint HeaderSize;
        public uint EntryCount;
        public uint EntrySize;
        public uint Time;
    }
}
