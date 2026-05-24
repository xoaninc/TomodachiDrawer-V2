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

// Report byte 0 holds the first 8 buttons (bit0=Y, bit1=B, bit2=A, bit3=X, ...)
// matching the RP2040 firmware's report layout [Btn1,Btn2,DPad,LX,LY,RX,RY,Pad].
static string DescribeReport(byte[] r)
{
    if (r.Length < 8) return $"short report ({r.Length} bytes)";
    return $"Btn1=0x{r[0]:X2} Btn2=0x{r[1]:X2} DPad={r[2]} " +
           $"LX={r[3]} LY={r[4]} RX={r[5]} RY={r[6]}";
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
    string? lastNonNeutral = null;
    int seen = 0;
    while (DateTime.UtcNow < deadline)
    {
        byte[] buf;
        try { buf = stream.Read(); }
        catch (TimeoutException) { continue; }

        // Neutral = no buttons, dpad 8, sticks centred. Only print changes.
        bool anyButton = buf.Length >= 2 && (buf[0] != 0 || buf[1] != 0);
        if (anyButton)
        {
            string desc = DescribeReport(buf);
            if (desc != lastNonNeutral)
            {
                Console.WriteLine($"  input: {desc}");
                lastNonNeutral = desc;
            }
            seen++;
        }
    }
    Console.WriteLine(seen > 0
        ? $"Observed {seen} non-neutral report(s) - playback confirmed."
        : "No button activity observed (device may be idle / done / no .tdld flashed).");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Note: could not open device for reading: {ex.Message}");
    // Enumeration still succeeded, so don't fail the test on read errors.
}

return 0;
