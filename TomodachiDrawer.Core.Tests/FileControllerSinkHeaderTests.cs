using System.IO;
using TomodachiDrawer.Core.Interfaces;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Pins the <c>.tdld</c> header, because the firmware reads it by fixed offset and gets no
    /// second chance: a header one byte off makes the board interpret the first record as garbage.
    /// <para>
    /// The v3 assertions matter more than the v4 ones. v4 is new and opt-in, but v3 is what every
    /// board already flashed in the field speaks, and it must not have moved by a single byte while
    /// v4 was being added.
    /// </para>
    /// </summary>
    public class FileControllerSinkHeaderTests
    {
        /// <summary>Writes the same short input through a sink and returns the file's bytes.</summary>
        private static byte[] Emit(Func<string, FileControllerSink> make)
        {
            var path = Path.Combine(Path.GetTempPath(), $"tdld-test-{Guid.NewGuid():N}.tdld");
            try
            {
                var sink = make(path);
                // Through the interface: Tap is a default interface method, so it does not exist
                // on the concrete type.
                ISwitchOutput output = sink;
                output.Tap(Button.A);
                output.Tap(DPad.RIGHT);
                output.Delay(100);
                sink.Dispose();
                return File.ReadAllBytes(path);
            }
            finally
            {
                try
                {
                    File.Delete(path);
                }
                catch
                { /* best effort */
                }
            }
        }

        [Fact]
        public void V3_header_is_unchanged()
        {
            var bytes = Emit(p => new FileControllerSink(p));

            Assert.Equal((byte)'T', bytes[0]);
            Assert.Equal((byte)'D', bytes[1]);
            Assert.Equal((byte)'L', bytes[2]);
            Assert.Equal((byte)'D', bytes[3]);
            Assert.Equal(FileControllerSink.VersionMillisecondTaps, bytes[4]);
            Assert.Equal(3, bytes[4]); // spelled out: the on-wire number, not just the constant
            Assert.Equal(0, bytes[5]); // padding, keeping the header at 6 bytes
        }

        [Fact]
        public void V4_header_carries_the_poll_counts_little_endian()
        {
            // Deliberately not the defaults, and deliberately wider than a byte, so a wrong offset
            // or a byte-order slip cannot pass by coincidence.
            var bytes = Emit(p => new FileControllerSink(p, 0x1234, 0x0056));

            Assert.Equal(FileControllerSink.VersionPollTaps, bytes[4]);
            Assert.Equal(4, bytes[4]);
            Assert.Equal(0x34, bytes[5]); // hold, low byte first
            Assert.Equal(0x12, bytes[6]);
            Assert.Equal(0x56, bytes[7]); // release
            Assert.Equal(0x00, bytes[8]);
            for (int i = 9; i < 16; i++)
                Assert.Equal(0, bytes[i]); // padding out to 16
        }

        [Fact]
        public void Only_the_header_differs_between_versions()
        {
            // The whole design rests on this: v4 changes how the firmware TIMES a tap, never which
            // records it is told to replay. If the bodies ever diverge, the two modes are drawing
            // different pictures and the A/B experiment they exist for is worthless.
            var v3 = Emit(p => new FileControllerSink(p));
            var v4 = Emit(p => new FileControllerSink(p, 22, 3));

            Assert.Equal(v3.Length - 6, v4.Length - 16);
            Assert.Equal(v3[6..], v4[16..]);
        }
    }
}
