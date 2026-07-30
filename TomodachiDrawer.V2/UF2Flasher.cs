using System.Buffers.Binary;

namespace TomodachiDrawer.UI.Avalonia;

internal enum RPChipType
{
    RP2040,
    RP2350,
}

internal static class UF2Flasher
{
    // This is repeated code so i would like to make it shared in .Core later.
    public static byte[] BuildTDLDUF2(byte[] tdldData, RPChipType chip)
    {
        const int MaxTDLDSize = 1 * 1024 * 1024;
        if (tdldData.Length > MaxTDLDSize)
            throw new ArgumentException(
                $"TDLD data exceeds maximum size of {MaxTDLDSize} bytes. This will shoot past the end of the flash!"
            );
        const uint TargetBase = 0x10100000u; // 1MB into flash, so 1MB limit — same layout on both chips.
        const uint PayloadSize = 256u;
        uint familyId = chip == RPChipType.RP2350 ? 0xe48bff57u : 0xE48BFF56u;

        int blockCount = (tdldData.Length + (int)PayloadSize - 1) / (int)PayloadSize;
        byte[] output = new byte[blockCount * 512];

        for (int i = 0; i < blockCount; i++)
        {
            var block = output.AsSpan(i * 512, 512);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x000..], 0x0A324655);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x004..], 0x9E5D5157);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x008..], 0x00002000);
            BinaryPrimitives.WriteUInt32LittleEndian(
                block[0x00C..],
                TargetBase + (uint)(i * PayloadSize)
            );
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x010..], PayloadSize);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x014..], (uint)i);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x018..], (uint)blockCount);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x01C..], familyId);
            BinaryPrimitives.WriteUInt32LittleEndian(block[0x1FC..], 0x0AB16F30);

            int srcOffset = i * (int)PayloadSize;
            int copyLen = Math.Min((int)PayloadSize, tdldData.Length - srcOffset);
            tdldData.AsSpan(srcOffset, copyLen).CopyTo(block[0x020..]);
        }

        return output;
    }

    public static string? FindRP2040Drive() => FindDriveByLabel("RPI-RP2");

    public static string? FindRP2350Drive() => FindDriveByLabel("RP2350");

    public static string? FindDriveForChip(RPChipType chip) =>
        chip == RPChipType.RP2350 ? FindRP2350Drive() : FindRP2040Drive();

    private static string? FindDriveByLabel(string label)
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.IsReady && drive.VolumeLabel == label)
                    return drive.RootDirectory.FullName;
            }
            catch { }
        }

        // Fallback for Linux where volume labels may not surface through DriveInfo
        if (OperatingSystem.IsLinux())
        {
            foreach (var baseDir in new[] { "/media", "/run/media" })
            {
                if (!Directory.Exists(baseDir))
                    continue;
                foreach (var userDir in Directory.GetDirectories(baseDir))
                {
                    var candidate = Path.Combine(userDir, label);
                    if (Directory.Exists(candidate))
                        return candidate + Path.DirectorySeparatorChar;
                }
            }
        }
        // Fallback for macOS
        else if (OperatingSystem.IsMacOS())
        {
            var candidate = $"/Volumes/{label}";
            if (Directory.Exists(candidate))
                return candidate + Path.DirectorySeparatorChar;
        }

        return null;
    }
}
