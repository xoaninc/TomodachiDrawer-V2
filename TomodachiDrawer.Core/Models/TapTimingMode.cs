// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>

namespace TomodachiDrawer.Core.Models
{
    /// <summary>
    /// How the firmware measures the length of a tap.
    /// <para>
    /// This is a desync lever, not a speed one. Every long draw this project has attempted has
    /// eventually shifted, and the leading explanation is dropped taps: the firmware presses a
    /// button, sleeps 25ms and releases, which silently assumes the console polled the HID endpoint
    /// during those 25ms. A console busy rendering may not have. One dropped tap is one missing
    /// cursor step, and from that moment every later stroke lands one cell off.
    /// </para>
    /// </summary>
    public enum TapTimingMode
    {
        /// <summary>
        /// Hold and release for a fixed 25ms each, timed by the board. The behaviour of every
        /// release so far, and what all firmware in the field speaks.
        /// </summary>
        Milliseconds = 0,

        /// <summary>
        /// Hold and release for a number of <b>host polls</b> — the firmware re-sends the report
        /// and waits until the console has actually asked for it the requested number of times, so
        /// a tap cannot pass unobserved.
        /// <para>
        /// <b>Experimental, and RP2040/RP2350 only.</b> It changes the <c>.tdld</c> header, and the
        /// ESP32 firmware refuses a file whose version it does not recognise. Upstream shelved the
        /// idea with "NOTE: Is slower..?" — a verdict about throughput, which is not the problem
        /// being solved here.
        /// </para>
        /// </summary>
        HidPolls = 1,
    }
}
