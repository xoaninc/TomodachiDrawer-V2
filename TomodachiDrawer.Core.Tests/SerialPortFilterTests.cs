using TomodachiDrawer.Core;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Pins <see cref="SerialPortFilter"/>, which exists because
    /// <c>SerialPort.GetPortNames()</c> is not a list of devices.
    /// <para>
    /// The real report that prompted this: a Mac with <b>no board attached</b> offered six serial
    /// ports — two Bluetooth pseudo-ports, a paired pair of headphones, and Apple's debug console,
    /// each in both <c>cu.</c> and <c>tty.</c> flavours — and the UI said "6 serial port(s) — pick
    /// the one in download mode".
    /// </para>
    /// <para>
    /// Platform is passed in rather than detected, so every rule is checkable from any machine.
    /// </para>
    /// </summary>
    public class SerialPortFilterTests
    {
        /// <summary>Exactly what was on screen, so the regression is the real one.</summary>
        private static readonly string[] MacNoBoardAttached =
        [
            "/dev/cu.Bluetooth-Incoming-Port",
            "/dev/cu.BoseQCUltra2HP",
            "/dev/cu.debug-console",
            "/dev/tty.Bluetooth-Incoming-Port",
            "/dev/tty.BoseQCUltra2HP",
            "/dev/tty.debug-console",
        ];

        [Fact]
        public void A_mac_with_nothing_plugged_in_offers_no_ports()
        {
            var kept = SerialPortFilter.LikelyBoards(
                MacNoBoardAttached,
                out var hidden,
                PortPlatform.MacOs
            );

            Assert.Empty(kept);
            Assert.Equal(6, hidden.Length); // and all six are reportable, none vanish silently
        }

        [Fact]
        public void An_esp32_is_found_among_the_noise()
        {
            var ports = MacNoBoardAttached.Append("/dev/cu.usbmodem2101").ToArray();

            var kept = SerialPortFilter.LikelyBoards(ports, PortPlatform.MacOs);

            Assert.Equal(["/dev/cu.usbmodem2101"], kept);
        }

        /// <summary>
        /// The common UART bridges, since not every board presents native USB CDC. Getting this
        /// list wrong hides a real device, which is worse than showing a fake one.
        /// </summary>
        [Theory]
        [InlineData("/dev/cu.usbmodem14201")] // ESP32-S3 native USB
        [InlineData("/dev/cu.usbserial-0001")] // CP210x / FTDI
        [InlineData("/dev/cu.wchusbserial56780038171")] // CH340 / CH9102
        [InlineData("/dev/cu.SLAB_USBtoUART")] // Silicon Labs' own driver
        public void Usb_serial_bridges_are_all_recognised(string port)
        {
            Assert.Equal([port], SerialPortFilter.LikelyBoards([port], PortPlatform.MacOs));
        }

        /// <summary>
        /// <c>/dev/tty.*</c> blocks on open waiting for carrier detect, which a bare USB-serial
        /// board never asserts — so offering it is offering a port that hangs. Where both views of
        /// one device exist, only <c>cu.</c> survives.
        /// </summary>
        [Fact]
        public void Only_the_cu_view_of_a_device_is_offered()
        {
            var kept = SerialPortFilter.LikelyBoards(
                ["/dev/tty.usbmodem2101", "/dev/cu.usbmodem2101"],
                PortPlatform.MacOs
            );

            Assert.Equal(["/dev/cu.usbmodem2101"], kept);
        }

        /// <summary>But a tty with no cu counterpart is still better than nothing.</summary>
        [Fact]
        public void A_tty_with_no_cu_counterpart_is_kept()
        {
            var kept = SerialPortFilter.LikelyBoards(
                ["/dev/tty.usbserial-A9007UX1"],
                PortPlatform.MacOs
            );

            Assert.Equal(["/dev/tty.usbserial-A9007UX1"], kept);
        }

        [Fact]
        public void Windows_com_ports_pass_through_untouched()
        {
            var kept = SerialPortFilter.LikelyBoards(
                ["COM3", "COM1", "COM11"],
                PortPlatform.Windows
            );

            Assert.Equal(["COM1", "COM11", "COM3"], kept); // ordinal sort, deliberately stable
        }

        [Theory]
        [InlineData("/dev/ttyUSB0")]
        [InlineData("/dev/ttyACM0")]
        public void Linux_usb_serial_is_recognised(string port)
        {
            Assert.Equal([port], SerialPortFilter.LikelyBoards([port], PortPlatform.Linux));
        }

        [Fact]
        public void Linux_onboard_uarts_are_not_offered_as_boards()
        {
            Assert.Empty(
                SerialPortFilter.LikelyBoards(["/dev/ttyS0", "/dev/ttyS1"], PortPlatform.Linux)
            );
        }

        /// <summary>
        /// An unknown platform must not hide anything. Being wrong about which port is a board is
        /// recoverable; hiding the only working one is not.
        /// </summary>
        [Fact]
        public void An_unknown_platform_hides_nothing_except_known_junk()
        {
            var kept = SerialPortFilter.LikelyBoards(
                ["/dev/weird0", "/dev/cu.debug-console"],
                PortPlatform.Other
            );

            Assert.Equal(["/dev/weird0"], kept);
        }

        [Fact]
        public void Duplicates_blanks_and_null_input_are_survivable()
        {
            Assert.Empty(SerialPortFilter.LikelyBoards(null!, PortPlatform.MacOs));
            Assert.Equal(
                ["/dev/cu.usbmodem1"],
                SerialPortFilter.LikelyBoards(
                    ["/dev/cu.usbmodem1", "/dev/cu.usbmodem1", "", "   "],
                    PortPlatform.MacOs
                )
            );
        }

        /// <summary>Kept and hidden must partition the input — no port counted twice or lost.</summary>
        [Fact]
        public void Kept_and_hidden_partition_the_input()
        {
            var ports = MacNoBoardAttached
                .Append("/dev/cu.usbmodem2101")
                .Append("/dev/tty.usbmodem2101")
                .ToArray();

            var kept = SerialPortFilter.LikelyBoards(ports, out var hidden, PortPlatform.MacOs);

            Assert.Equal(ports.Length, kept.Length + hidden.Length);
            Assert.Empty(kept.Intersect(hidden));
            Assert.Equal(
                ports.OrderBy(p => p).ToArray(),
                kept.Concat(hidden).OrderBy(p => p).ToArray()
            );
        }
    }
}
