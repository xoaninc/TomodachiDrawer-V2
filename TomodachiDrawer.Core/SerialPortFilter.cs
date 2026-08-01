// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

namespace TomodachiDrawer.Core
{
    /// <summary>
    /// Narrows <c>SerialPort.GetPortNames()</c> down to ports a microcontroller could plausibly be
    /// on.
    /// <para>
    /// The raw list is not a list of devices. On macOS it includes
    /// <c>/dev/cu.Bluetooth-Incoming-Port</c>, <c>/dev/cu.debug-console</c> and an entry for every
    /// Bluetooth audio device ever paired — so a machine with no board attached at all still reports
    /// six "serial ports", and the user is asked to pick the right one out of six wrong answers.
    /// </para>
    /// <para>
    /// Pure string work, deliberately: it is the part worth testing, and it keeps the platform rules
    /// in one place instead of spread across the polling loop and the flasher's port probe.
    /// </para>
    /// </summary>
    public static class SerialPortFilter
    {
        /// <summary>
        /// Names that are never a board. Matched case-insensitively as substrings of the port name,
        /// because macOS prefixes them with both <c>cu.</c> and <c>tty.</c>.
        /// </summary>
        private static readonly string[] NeverADevice =
        [
            "Bluetooth-Incoming-Port",
            "debug-console",
            "wlan-debug", // iOS wireless debugging, appears with Xcode installed
        ];

        /// <summary>
        /// Fragments that identify a USB serial device on macOS. Covers the ESP32-S3's native USB
        /// CDC (<c>usbmodem</c>) and the common UART bridges: CP210x and FTDI (<c>usbserial</c>),
        /// CH340/CH9102 (<c>wchusbserial</c>), and Silicon Labs' own driver naming.
        /// </summary>
        private static readonly string[] MacUsbFragments =
        [
            "usbmodem",
            "usbserial",
            "wchusbserial",
            "SLAB_USBtoUART",
            "usbmodemserial",
        ];

        /// <summary>
        /// Filters and orders the ports. Never throws; an unrecognised platform passes everything
        /// through rather than hiding a device we simply do not have a rule for.
        /// </summary>
        /// <param name="ports">Raw names, e.g. from <c>SerialPort.GetPortNames()</c>.</param>
        /// <param name="hidden">
        /// Ports that were filtered out, so the caller can say so. Nothing should disappear from a
        /// device picker without the log being able to explain why.
        /// </param>
        public static string[] LikelyBoards(
            IEnumerable<string> ports,
            out string[] hidden,
            PortPlatform? platform = null
        )
        {
            var all = (ports ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct().ToArray();
            var os = platform ?? Current();

            var keep = all.Where(p => IsLikelyBoard(p, os)).ToList();

            if (os == PortPlatform.MacOs)
            {
                // /dev/tty.* blocks on open until carrier detect, which for a bare USB-serial board
                // never arrives — /dev/cu.* is the one that works. Where both exist for the same
                // device, keeping tty. would be offering a port that hangs.
                var cuNames = keep.Where(IsCu)
                    .Select(p => DeviceKey(p))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                keep.RemoveAll(p => !IsCu(p) && cuNames.Contains(DeviceKey(p)));
            }

            var kept = keep.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            hidden = all.Except(kept).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
            return kept;
        }

        /// <summary>Convenience overload for callers that do not report what was hidden.</summary>
        public static string[] LikelyBoards(
            IEnumerable<string> ports,
            PortPlatform? platform = null
        ) => LikelyBoards(ports, out _, platform);

        private static bool IsLikelyBoard(string port, PortPlatform os)
        {
            foreach (var junk in NeverADevice)
                if (port.Contains(junk, StringComparison.OrdinalIgnoreCase))
                    return false;

            return os switch
            {
                // COM ports are already a list of real devices, and there is no naming convention
                // to key off, so anything that survives the denylist stays.
                PortPlatform.Windows => true,

                PortPlatform.MacOs => MacUsbFragments.Any(f =>
                    port.Contains(f, StringComparison.OrdinalIgnoreCase)
                ),

                // ttyUSB = USB-serial bridge, ttyACM = USB CDC (what the ESP32-S3 presents natively).
                PortPlatform.Linux => port.Contains("ttyUSB", StringComparison.OrdinalIgnoreCase)
                    || port.Contains("ttyACM", StringComparison.OrdinalIgnoreCase),

                _ => true,
            };
        }

        private static bool IsCu(string port) =>
            port.Contains("/cu.", StringComparison.OrdinalIgnoreCase)
            || port.StartsWith("cu.", StringComparison.OrdinalIgnoreCase);

        /// <summary>The device name with the cu./tty. prefix stripped, so the two views of one
        /// physical device compare equal.</summary>
        private static string DeviceKey(string port)
        {
            int slash = port.LastIndexOf('/');
            var leaf = slash >= 0 ? port[(slash + 1)..] : port;

            if (leaf.StartsWith("cu.", StringComparison.OrdinalIgnoreCase))
                return leaf[3..];
            if (leaf.StartsWith("tty.", StringComparison.OrdinalIgnoreCase))
                return leaf[4..];
            return leaf;
        }

        private static PortPlatform Current()
        {
            if (OperatingSystem.IsWindows())
                return PortPlatform.Windows;
            if (OperatingSystem.IsMacOS())
                return PortPlatform.MacOs;
            if (OperatingSystem.IsLinux())
                return PortPlatform.Linux;
            return PortPlatform.Other;
        }
    }

    /// <summary>Passed explicitly by tests so the rules are checkable off their own platform.</summary>
    public enum PortPlatform
    {
        Windows,
        MacOs,
        Linux,
        Other,
    }
}
