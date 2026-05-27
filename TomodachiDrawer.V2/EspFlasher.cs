// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

using System.IO.Ports;
using System.Security.Cryptography;
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

    // The ESPTool NuGet writes flash in 1 KB blocks with NO per-block retry and
    // leaves a partial write on the first dropped block (FlashData throws after
    // FlashBegin has already erased the whole region). A single flaky
    // USB-Serial-JTAG block therefore used to leave a truncated .tdld — an
    // unwritten 0xFF gap — which the firmware replays as a runaway RLE repeat and
    // hangs on (last layer never finishes, LED stuck). We re-flash up to this many
    // times and verify every attempt with the device-side flash MD5.
    private const int MaxFlashAttempts = 3;

    /// <summary>Flashes a .tdld program to the data partition.</summary>
    public static Task FlashTdldAsync(string serialPort, byte[] tdldData,
                                      IProgress<float>? progress = null,
                                      CancellationToken ct = default,
                                      Action<string>? log = null)
    {
        if (tdldData.Length > TdldPartitionSize)
            throw new ArgumentException(
                $"TDLD data exceeds the {TdldPartitionSize} byte partition. " +
                "This would overflow the ESP32-S3 tdld partition!");
        return UploadAtOffsetAsync(serialPort, TdldPartitionOffset, tdldData, progress, ct, log);
    }

    /// <summary>Flashes the merged base firmware image (bootloader+parttable+app).</summary>
    public static Task FlashBaseFirmwareAsync(string serialPort, byte[] mergedFirmware,
                                              IProgress<float>? progress = null,
                                              CancellationToken ct = default,
                                              Action<string>? log = null)
        => UploadAtOffsetAsync(serialPort, MergedFirmwareOffset, mergedFirmware, progress, ct, log);

    private static async Task UploadAtOffsetAsync(string serialPort, uint offset, byte[] data,
                                                  IProgress<float>? progress, CancellationToken ct,
                                                  Action<string>? log = null)
    {
        // MD5 of the exact bytes we intend to flash. We compare this against the
        // device's flash MD5 over the FULL expected length, so a write that wrote
        // fewer bytes than `data` (the truncation bug) is detected, not trusted.
        byte[] expectedMd5 = MD5.HashData(data);

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

            // Run the flasher stub (softloader) once: the ESP32-S3 ROM bootloader
            // rejects FLASH_BEGIN ("FlashBegin failed Invalid"), so flashing must go
            // through the stub, UNCOMPRESSED (ESPTool's deflated path truncates large
            // writes). Retries below reuse this stub — it stays responsive after a
            // failed FlashData, so we avoid the flaky USB-Serial-JTAG re-reset.
            SoftLoader soft = await toolbox.StartSoftloaderAsync(comm, loader, chip, ct);

            Exception? lastError = null;
            for (int attempt = 1; attempt <= MaxFlashAttempts; attempt++)
            {
                try
                {
                    await FlashRegionAsync(soft, offset, data, progress, log, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // A block that failed all its retries aborts this attempt; the
                    // MD5 check below confirms the region is incomplete and we re-flash.
                    lastError = ex;
                    log?.Invoke($"ESP32 flash attempt {attempt}/{MaxFlashAttempts} aborted: {ex.Message}");
                }

                // Verify the ENTIRE expected region via the device's own flash MD5.
                // This is what catches a truncated/partial write (the 0xFF gap).
                byte[] deviceMd5;
                try
                {
                    deviceMd5 = await soft.SPI_FLASH_MD5(offset, (uint)data.Length, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Stub stopped responding after a partial write: we can neither
                    // verify nor safely retry, and the board now holds an incomplete
                    // image — fail loudly instead of leaving a silently-broken flash.
                    throw new IOException(
                        "ESP32 flash failed and could not be verified — the board may hold an "
                        + "incomplete .tdld. Re-export in download mode (hold BOOT, tap RESET, "
                        + "release BOOT).", ex);
                }

                if (Md5Equal(deviceMd5, expectedMd5))
                {
                    await toolbox.ResetDeviceAsync(comm, ct);
                    return; // verified-complete write
                }
                log?.Invoke($"ESP32 flash attempt {attempt}/{MaxFlashAttempts}: written region failed "
                            + "MD5 verification, re-flashing.");
                // MD5 mismatch => truncated/corrupt write; loop and re-flash.
            }

            throw new IOException(
                $"ESP32 flash verification kept failing after {MaxFlashAttempts} attempts (MD5 "
                + "mismatch) — the write keeps coming out incomplete. Re-export in download mode "
                + "and try again.", lastError);
        }
        finally
        {
            toolbox.CloseSerial(comm);
        }
    }

    // Writes `data` to flash at `offset` using the softloader's low-level
    // FlashBegin/FlashData directly, bypassing the NuGet's UploadFlashTool (which
    // has no per-block retry and silently stops on a short stream read). Each block
    // is sent straight from the source array and retried on transient failures, so a
    // flaky USB-Serial-JTAG block no longer leaves a truncated image.
    private static async Task FlashRegionAsync(SoftLoader soft, uint offset, byte[] data,
                                               IProgress<float>? progress, Action<string>? log,
                                               CancellationToken ct)
    {
        const uint blockSize = 1024;
        const int blockRetries = 4;
        uint size = (uint)data.Length;
        uint blocks = (size + blockSize - 1) / blockSize;

        await soft.FlashBeginAsync(size, blocks, blockSize, offset, ct);

        for (uint i = 0; i < blocks; i++)
        {
            uint start = i * blockSize;
            uint len = Math.Min(blockSize, size - start);
            byte[] block = new byte[len];
            Array.Copy(data, start, block, 0, (int)len);

            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    await soft.FlashDataAsync(block, i, ct);
                    break;
                }
                catch (Exception ex) when (attempt < blockRetries && ex is not OperationCanceledException)
                {
                    log?.Invoke($"  ESP32 block {i + 1}/{blocks} write failed (try {attempt}): {ex.Message} — retrying");
                }
            }
            progress?.Report((float)(i + 1) / blocks);
        }

        // Finish the write so the stub FLUSHES its final (partial) flash page. The
        // NuGet's UploadFlashTool skips FLASH_END ("not required"), which left the
        // last page uncommitted — a 0xFF tail with no EOF byte. THAT is the actual
        // truncation that hangs the firmware. Flag 1 = finish WITHOUT rebooting, so
        // the device-side MD5 verification can still run afterwards.
        await soft.FlashEndAsync(1, 0, ct);
    }

    private static bool Md5Equal(byte[] a, byte[] b)
    {
        if (a.Length < 16 || b.Length < 16)
            return false;
        for (int i = 0; i < 16; i++)
            if (a[i] != b[i])
                return false;
        return true;
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
