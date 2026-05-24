// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).
//
// Integration test for the ESP32-S3 firmware's USB HID enumeration.
//
//   dotnet run --project tests/integration            # enumeration only
//   dotnet run --project tests/integration -- --read 10   # also read reports
//
// Exit code 0 = the Pokken Pad device (VID 0x0F0D / PID 0x0092) was found.
// With --read N it additionally opens the device and prints any non-neutral
// input reports it observes for N seconds - a Switch-free way to confirm the
// firmware is actually streaming a .tdld program as HID inputs.

using HidSharp;

const int VID = 0x0F0D;
const int PID = 0x0092;

// On Windows HidSharp returns the report with a leading 1-byte Report ID
// (0x00 when the device declares no report IDs), so the 8 data bytes
// [Btn1,Btn2,DPad,LX,LY,RX,RY,Pad] start at index 1. d() resolves a data
// index to its buffer offset, tolerating builds/platforms with no prefix.
static string DescribeReport(byte[] r)
{
    int off = r.Length >= 9 ? 1 : 0;   // skip Report ID prefix if present
    byte D(int i) => (off + i) < r.Length ? r[off + i] : (byte)0;
    return $"Btn1=0x{D(0):X2} Btn2=0x{D(1):X2} DPad={D(2)} " +
           $"LX={D(3)} LY={D(4)} RX={D(5)} RY={D(6)}";
}

// True if any of the 16 buttons are held, accounting for the Report ID prefix.
static bool AnyButton(byte[] r)
{
    int off = r.Length >= 9 ? 1 : 0;
    return r.Length > off + 1 && (r[off] != 0 || r[off + 1] != 0);
}

var devices = DeviceList.Local.GetHidDevices(VID, PID).ToList();
if (devices.Count == 0)
{
    Console.Error.WriteLine($"FAIL: no HID device with VID 0x{VID:X4} PID 0x{PID:X4} enumerated.");
    Console.Error.WriteLine("Is the ESP32-S3 plugged into the USB port (not UART) and running the firmware?");
    return 1;
}

var dev = devices[0];
string manufacturer = "?", product = "?";
try { manufacturer = dev.GetManufacturer(); } catch { }
try { product = dev.GetProductName(); } catch { }
Console.WriteLine($"OK: found {manufacturer} / {product}");
Console.WriteLine($"     {dev.DevicePath}");

// Optional report-reading phase.
int readSeconds = 0;
for (int i = 0; i < args.Length; i++)
{
    if (args[i] == "--read" && i + 1 < args.Length && int.TryParse(args[i + 1], out var n))
        readSeconds = n;
}
if (readSeconds <= 0)
    return 0;

Console.WriteLine($"Reading input reports for {readSeconds}s (press buttons in the .tdld will show here)...");
try
{
    using var stream = dev.Open();
    stream.ReadTimeout = 500;
    var deadline = DateTime.UtcNow.AddSeconds(readSeconds);
    int totalReads = 0, timeouts = 0, withButton = 0, dumped = 0;
    string? lastDesc = null;
    while (DateTime.UtcNow < deadline)
    {
        byte[] buf;
        try { buf = stream.Read(); }
        catch (TimeoutException) { timeouts++; continue; }

        totalReads++;
        // Dump the raw bytes of the first few reports for diagnostics.
        if (dumped < 6)
        {
            Console.WriteLine($"  raw[{buf.Length}]: {BitConverter.ToString(buf)}");
            dumped++;
        }

        if (AnyButton(buf))
        {
            withButton++;
            string desc = DescribeReport(buf);
            if (desc != lastDesc)
            {
                Console.WriteLine($"  input: {desc}");
                lastDesc = desc;
            }
        }
    }
    Console.WriteLine($"Summary: {totalReads} reports read, {timeouts} timeouts, {withButton} with a button held.");
    if (withButton > 0)
    {
        Console.WriteLine("Playback confirmed (saw button activity).");
    }
    else
    {
        Console.WriteLine("No button activity observed. The device may be idle, already finished");
        Console.WriteLine("its .tdld program (LED rainbow), or have no .tdld flashed. Re-flash a");
        Console.WriteLine("program that holds a button and reset the board, then read again.");
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Note: could not open device for reading: {ex.Message}");
    // Enumeration still succeeded, so don't fail the test on read errors.
}

return 0;
