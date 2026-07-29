# Future plan — "computer as controller" over Bluetooth (NXBT)

**Status:** decided, not started. Scheduled **after** the upstream sync and after the
Layer-1 renderer. Researched 2026-07-29.

Decision: **do both, exporter first.** (1) a `.tdld` → NXBT macro transcoder with an
"Export NXBT macro" button, then (2) a live `BluetoothSink : ISwitchOutput`.

This document exists so the reasoning doesn't have to be re-derived, including the case
*against* putting it on the production path — which was raised twice and overruled. It is
recorded, not open.

---

## 1. What this is

Instead of a microcontroller pretending to be a USB controller, a **computer pretends to be
a Bluetooth Pro Controller**. Established technique with a direct precedent for our exact
use case:

| Project | What it is |
|---|---|
| [NXBT](https://github.com/Brikwerk/nxbt) | Pro Controller emulation over Bluetooth; macro language with loops, plus a Python API |
| [joycontrol](https://github.com/mart1nro/joycontrol) / [Poohl fork](https://github.com/Poohl/joycontrol) | Same idea, JOYCON_L/R/PRO, Linux + BlueZ |
| ⭐ [img2splat](https://github.com/JonathanNye/img2splat) | Converts an image into NXBT macros to draw it in Splatoon 3 — **architecturally TomodachiDrawer over Bluetooth** |

## 2. What it does not solve, and the risks

Recorded honestly so expectations are calibrated:

1. **It does not remove external hardware — it changes which.** NXBT needs Linux + BlueZ. On
   macOS/Windows the [official path](https://github.com/Brikwerk/nxbt/blob/master/docs/Windows-and-macOS-Installation.md)
   is a VirtualBox + Vagrant VM, and a **USB Bluetooth dongle is mandatory**: *"internal
   Bluetooth adapters are incompatible (generally) with the process that allows a VM to use
   external resources."* A Mac's built-in Bluetooth cannot be used, and VirtualBox on Apple
   Silicon is a further blocker.
2. **It regresses reliability in the exact dimension this project already fights.** img2splat
   documents *"issues with running very long macros with NXBT where it skips inputs"* and
   *"risk of synchronization loss if Bluetooth disconnects mid-execution."* Our draws run for
   hours and we already fight desyncs over a **wire**.
3. **Switch firmware 12.0.0 broke every software emulator**; forks patched it with mixed
   results. Pairing needs the "Change Grip/Order" menu and is inconsistent by nature — the
   Switch processes packets slowly in that menu.
4. **Switch 2 support is unconfirmed.** Some report success, others report the Switch 2 not
   detecting the controller at all. V2 is validated on Switch 2, so this is the risky end.
5. **It still needs a real Switch.** It removes the *board*, not the *console* — and the board
   is the cheap part. The Layer-1 renderer is what actually removes hardware from the
   iteration loop.

**Target machine:** the **Windows PC**, not the Mac (see 1). A **Raspberry Pi Zero 2 W**
avoids the VM entirely — native Linux, built-in Bluetooth — and is the tidiest host.

**Do not buy the dongle before Phase 1 is done.** The whole exporter is built and validated
without it.

### Why "write the data into a real controller" cannot work

Joy-Cons and Pro Controllers *do* have writable SPI flash (calibration, colours — see
[dekuNukem's reverse engineering](https://github.com/dekuNukem/Nintendo_Switch_Reverse_Engineering)),
so you can write *to* them. But their firmware has no input-playback mechanism, and the
buttons are scanned in a hardware matrix by the onboard MCU. There is nowhere to put a
`.tdld` and nothing that would replay it. A controller also pairs to exactly one host, so
"double connected" isn't available either.

---

## 3. Prerequisite

**The Layer-1 renderer** (`V2_IMPROVEMENT_PLAN.md` §A.3) is required, not optional: it is the
only way to verify an emitted macro without hardware. Parse the macro we produced, replay it
onto the simulated canvas, diff against the source image.

---

## 4. Phase 1 — `.tdld` → NXBT macro exporter

New standalone transcoder plus an "Export NXBT macro" button. V2 never touches Bluetooth; it
writes a text file the user feeds to NXBT. Runs on Windows, macOS and Linux alike.

NXBT macro syntax (confirmed from `Brikwerk/nxbt/docs/Macros.md`):

```
A 0.1s                    # press for a duration, then release
A B 0.5s                  # same line = simultaneous
1s                        # bare duration = pause
L_STICK@-100+000 0.75s    # stick: signed 3-digit percent, BOTH axes in one token
LOOP 100                  # indented body, nesting supported
    B 0.1s
    0.1s
# comment
```

Buttons: `Y X B A PLUS MINUS HOME CAPTURE L_STICK_PRESS R_STICK_PRESS L ZL R ZR`.
D-Pad: `DPAD_UP DPAD_DOWN DPAD_LEFT DPAD_RIGHT`.

### The three real mismatches — this is the actual work

| # | Mismatch | Why it matters | Approach |
|---|---|---|---|
| 1 | **No press/release split.** NXBT lines are *press-for-a-duration*. Our format has separate `PRESS_BUTTON`/`RELEASE_BUTTON`, and `CanvasDrawer` **holds A across consecutive points 1 step apart** (`CanvasDrawer.cs:811-847`) — a core drawing optimisation, not a detail | A held A becomes either one long `A <total>s` (losing the interleaved DPad moves) or `A DPAD_RIGHT 0.05s` repeated, which **re-presses A every step**. The game may not treat a re-press as a continuous hold. **Top correctness risk**, and only hardware settles it — but the renderer can detect a broken hold if the trace models it | Emit the simultaneous-line form; keep hold-run length in the trace; make it a validation target |
| 2 | **Stick representation.** Ours: one axis at a time, `byte` 0–255, 128 = centre (`ISwitchOutput.SetStick`). NXBT: both axes in one token, signed −100..+100 | A per-opcode translation is impossible | Track current per-axis state; emit the combined `L_STICK@±XXX±YYY` on any change. Convert `(v−128)/127×100`, rounded |
| 3 | **Timing granularity.** Ours: 1 ms integer, max 4095 units per record. NXBT: decimal seconds, and the docs specify **no** minimum press duration or scheduler resolution | Our defaults are 25–50 ms taps, and img2splat's documented failure mode is long macros skipping inputs | Emit `0.025s`-style values, but make tap duration a **user-adjustable knob** from day one so it can be lengthened |

**Use `LOOP`.** Our `REPEAT_LAST_1`/`REPEAT_LAST_2` opcodes map naturally onto it, and file
size *is* the documented failure mode — so this is a correctness measure, not just tidiness.

### Acceptance — all offline, zero hardware

1. Round-trip through the Layer-1 renderer: parse the emitted macro, replay onto the
   simulated canvas, diff against the source image.
2. Golden-file tests per opcode class, including a held-A run and a stick move.
3. A 256² image produces a macro that parses under NXBT's own grammar.
4. Unit test the `byte` → signed-percent stick conversion at the boundaries (0, 128, 255).

### Out of our control — say so in the UI

Whether NXBT drops inputs across a multi-hour macro is its scheduler, not our code. Our
macro's correctness we can prove; that we cannot. Put a note next to the export button.

---

## 5. Phase 2 — live Bluetooth sink

`BluetoothSink : ISwitchOutput` driving NXBT's Python API, reusing Phase 1's mapping as the
translation layer.

- Requires C#↔Python interop.
- **Only runs with V2 executing inside Linux/BlueZ** — a Linux-only mode, not a
  cross-platform feature. Gate it in the UI accordingly.
- Cannot be tested without the dongle and a working VM, so it strictly follows Phase 1.

## 6. Why this fits the existing architecture

`ISwitchOutput` (`Core/OutputSinks/ISwitchOutput.cs`, 92 lines) already abstracts exactly
this, and Core already ships `DummySink`, `TimingSink` and `FileControllerSink`. `.tdld` is,
per the README, *"a generic way to represent inputs and delays in a compact form"* — so the
transcoder is a format translation, not an architectural change. It would also let someone
with **no microcontroller at all** use V2.
