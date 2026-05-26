# TomodachiDrawer.Firmware
This folder houses the Pico SDK .c code that runs on the RP2040 **and the RP2350**
(Raspberry Pi Pico 2 / RP2350-Zero). The same source builds for either chip; the only
board-specific difference is the neopixel byte order (RP2350-Zero uses RGB, RP2040-Zero
uses GRB), handled with a `PICO_RP2350` compile-time check.

To be candid, this went through a few versions before I got it working, some which were like 80% ai written.

The best part? The ai written versions were overly complicated and had horrifying timing problems.

The end version is pretty much all my code, except for the rainbow logic which i asked ai to write.

This is simple by design, it is loaded all into ram for speed to avoid the need for wrapping functions in stuff.

## Building
I built this through the vscode Pico SDK which did a lot of the installations on its own and seemed to generally just work™ so that was nice.

The board/platform is selected at configure time and the output `.uf2` is named per board:

```sh
# RP2040 (e.g. RP2040-Zero)
cmake -B build-rp2040 -DPICO_BOARD=waveshare_rp2040_zero -DPICO_PLATFORM=rp2040
cmake --build build-rp2040            # -> TomodachiDrawer.Firmware.rp2040.uf2

# RP2350 (e.g. RP2350-Zero / Raspberry Pi Pico 2)
cmake -B build-rp2350 -DPICO_BOARD=waveshare_rp2350_zero -DPICO_PLATFORM=rp2350
cmake --build build-rp2350            # -> TomodachiDrawer.Firmware.rp2350.uf2
```

CI builds both automatically and the released app bundles each, so end users normally don't
build this themselves.