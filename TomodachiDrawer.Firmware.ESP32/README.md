# TomodachiDrawer.Firmware.ESP32

ESP-IDF firmware that ports the TomodachiDrawer microcontroller side to the
**ESP32-S3** (e.g. ESP32-S3-DevKitC-1), running in parallel with the original
RP2040 firmware. It emulates a HORI Pokken Pad over USB HID and replays a
`.tdld` program stored in a dedicated flash partition, exactly like the RP2040
build — the Switch sees the same controller.

> Part of **TomodachiDrawer V2**, a modified version of
> [TomodachiDrawer](https://github.com/Lucas7yoshi/TomodachiDrawer) (original
> © Lucas7yoshi, GPL-3.0). ESP32-S3 port and fixes © 2026 Xoan
> (github.com/xoaninc). Licensed under GPL-3.0 — see [../LICENSE](../LICENSE).

## Hardware

- **ESP32-S3** with native USB OTG (the classic ESP32 + CP2102 will **not**
  work — its USB is a serial bridge with no USB-device capability).
- Tested on the **ESP32-S3-DevKitC-1**. Onboard NeoPixel on **GPIO 48**
  (override `LED_GPIO` in `main/main.c` if your board differs, e.g. GPIO 38 on
  some revisions, GPIO 35 on M5Stack AtomS3).

### The DevKitC-1 has two USB-C ports

- **UART** port: a USB-serial bridge (CP2102/CH340) — fine for flashing.
- **USB** port: the S3's native USB-Serial-JTAG / USB-OTG — use this one for
  the Switch (and it can flash too). The firmware switches this port to the
  Pokken Pad HID device once it boots.

You can flash over the **USB** port and then move the same cable to the Switch.
To enter download mode for flashing: **hold BOOT, tap RESET, release BOOT**.

## Requirements

- ESP-IDF **v5.3+** (developed against v6.0.1).
- For the host unit tests: a C compiler (clang/gcc, e.g. LLVM-MinGW).

## Build & flash

```sh
idf.py set-target esp32s3
idf.py build
idf.py -p <PORT> flash      # PORT e.g. COM4 (Windows) or /dev/ttyACM0
```

The flash layout (`partitions.csv`) matches the RP2040 1 MB data offset:

| Partition | Type | Offset    | Size  | Purpose                         |
|-----------|------|-----------|-------|---------------------------------|
| factory   | app  | 0x10000   | 960 KB| this firmware                   |
| tdld      | data | 0x100000  | 1 MB  | the `.tdld` program to replay   |

The drawing data is written to the `tdld` partition at `0x100000` — the
desktop app's "Export To ESP32!" button does this for you, or manually:

```sh
esptool --chip esp32s3 -p <PORT> write-flash 0x100000 your.tdld
```

## LED status

Same semantics as the RP2040 build: dim white = idle, green = a button is
held, fast red blink = bad/empty `.tdld` data, slow red = partition missing,
rainbow = finished drawing.

## Tests

The `.tdld` parser is pure C and tested two ways from one source
(`test/tdld_tests.c`):

- **Host (fast):** `test/run_host_tests.ps1` compiles it with clang and runs
  it natively (13 cases, 26 assertions).
- **On-target:** `test/on_target/` is a small ESP-IDF project that runs the
  same tests on the chip and prints PASS/FAIL over USB-Serial-JTAG.

A .NET HID enumeration/readback test lives in `tests/integration/`
(`dotnet run -- --read 15`).

## Notes / fixes vs the original

- The HID report descriptor here declares the **full 8-byte** Pokken input
  report (the original RP2040 descriptor declared 7 bytes while sending 8 — a
  spec mismatch the Switch tolerates but hosts that validate report length do
  not). This fix also benefits the RP2040 build.
- Console is routed over USB-Serial-JTAG so flashing and `idf.py monitor`
  share the one USB-C cable.
