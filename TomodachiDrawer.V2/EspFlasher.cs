// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

using System.IO.Ports;
using EspDotNet;
using EspDotNet.Communication;
using EspDotNet.Loaders;
using EspDotNet.Loaders.SoftLoader;
using EspDotNet.Tools;
using EspDotNet.Tools.Firmware;

namespace TomodachiDrawer.UI.Avalonia;

/// <summary>
/// Flashes the ESP32-S3 firmware and .tdld data partition using the native C#
/// ESPTool (KooleControls/ESPTool, NuGet "ESPTool", namespace EspDotNet). This
/// is the ESP32 counterpart to <see cref="UF2Flasher"/> for the RP2040.
///
/// The API shape here was verified by hand against the v3.0.3 source tag.
/// </summary>
internal static class EspFlasher
{
    // Must match TomodachiDrawer.Firmware.ESP32/partitions.csv exactly.
    public const uint TdldPartitionOffset = 0x100000;
    public const int TdldPartitionSize = 1 * 1024 * 1024;

    // Merged firmware image (bootloader + partition table + app) starts at 0x0.
    public const uint MergedFirmwareOffset = 0x0;

    private const int InitialBaud = 115200;

    /// <summary>Flashes a .tdld program to the data partition.</summary>
    public static Task FlashTdldAsync(string serialPort, byte[] tdldData,
                                      IProgress<float>? progress = null,
                                      CancellationToken ct = default)
    {
        if (tdldData.Length > TdldPartitionSize)
            throw new ArgumentException(
                $"TDLD data exceeds the {TdldPartitionSize} byte partition. " +
                "This would overflow the ESP32-S3 tdld partition!");
        return UploadAtOffsetAsync(serialPort, TdldPartitionOffset, tdldData, progress, ct);
    }

    /// <summary>Flashes the merged base firmware image (bootloader+parttable+app).</summary>
    public static Task FlashBaseFirmwareAsync(string serialPort, byte[] mergedFirmware,
                                              IProgress<float>? progress = null,
                                              CancellationToken ct = default)
        => UploadAtOffsetAsync(serialPort, MergedFirmwareOffset, mergedFirmware, progress, ct);

    private static async Task UploadAtOffsetAsync(string serialPort, uint offset, byte[] data,
                                                  IProgress<float>? progress, CancellationToken ct)
    {
        var toolbox = new ESPToolbox();
        Communicator comm = toolbox.CreateCommunicator();
        toolbox.OpenSerial(comm, serialPort, InitialBaud);
        try
        {
            ILoader loader = await toolbox.StartBootloaderAsync(comm, ct);
            ChipTypes chip = await toolbox.DetectChipTypeAsync(loader, ct);
            if (chip != ChipTypes.ESP32s3)
                throw new InvalidOperationException(
                    $"Expected an ESP32-S3 on {serialPort} but detected {chip}. " +
                    "Make sure the board is in download mode (hold BOOT, tap RESET).");

            // Run the flasher stub (softloader) first: the ESP32-S3 ROM
            // bootloader rejects FLASH_BEGIN here ("FlashBegin failed Invalid"),
            // so flashing must go through the stub like esptool/idf.py do.
            // Then upload UNCOMPRESSED: ESPTool's deflated path was observed to
            // truncate a ~290KB write (unbootable app), while uncompressed via
            // the stub writes the full image reliably. SoftLoader is an ILoader,
            // so CreateUploadFlashTool accepts it.
            SoftLoader soft = await toolbox.StartSoftloaderAsync(comm, loader, chip, ct);
            IUploadTool tool = toolbox.CreateUploadFlashTool(soft, chip);

            var firmware = new FirmwareProvider(
                entryPoint: 0,
                segments: new[] { new FirmwareSegmentProvider(offset, data) });

            await toolbox.UploadFirmwareAsync(tool, firmware, ct, progress);
            await toolbox.ResetDeviceAsync(comm, ct);
        }
        finally
        {
            toolbox.CloseSerial(comm);
        }
    }

    /// <summary>
    /// Best-effort detection of the ESP32-S3 serial port. Probes each serial
    /// port by attempting to start its ROM bootloader; the one that answers is
    /// an ESP chip in download mode. Returns null if none respond.
    /// </summary>
    public static async Task<string?> FindEsp32PortAsync(CancellationToken ct = default)
    {
        foreach (string name in SerialPort.GetPortNames())
        {
            if (await TryProbeAsync(name, ct))
                return name;
        }
        return null;
    }

    private static async Task<bool> TryProbeAsync(string serialPort, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));

            var toolbox = new ESPToolbox();
            Communicator comm = toolbox.CreateCommunicator();
            toolbox.OpenSerial(comm, serialPort, InitialBaud);
            try
            {
                _ = await toolbox.StartBootloaderAsync(comm, timeout.Token);
                return true;
            }
            finally
            {
                toolbox.CloseSerial(comm);
            }
        }
        catch
        {
            return false;
        }
    }
}
