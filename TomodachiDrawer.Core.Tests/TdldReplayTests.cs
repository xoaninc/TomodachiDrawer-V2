using TomodachiDrawer.Bench;
using TomodachiDrawer.Core.Interfaces;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    public class TdldReplayTests
    {
        private static byte[] Export(TimingSink route, bool polls)
        {
            string path = Path.Combine(Path.GetTempPath(), $"tdld-replay-{Guid.NewGuid():N}.tdld");
            try
            {
                using (
                    var file = polls
                        ? new FileControllerSink(path, 22, 3)
                        : new FileControllerSink(path)
                )
                    route.ReplayTo(file);
                return File.ReadAllBytes(path);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void One_recorded_route_has_identical_v3_v4_bodies_and_explicit_menu_durations()
        {
            using var route = new TimingSink();
            ISwitchOutput output = route;
            for (int i = 0; i < 18; i++)
                output.Tap(DPad.RIGHT);
            output.Tap(Button.A);
            output.Tap(Button.A, 100, 75);
            output.Tap(DPad.LEFT, 100, 75);
            output.Tap(Button.X, 200, 50);
            output.Delay(4100);
            output.Press(Button.ZR);
            output.SetStick(Stick.LX, 0);
            output.SetStick(Stick.RY, 255);
            output.ReleaseAll();

            byte[] v3 = Export(route, polls: false);
            byte[] v4 = Export(route, polls: true);
            Assert.Equal(v3[6..], v4[16..]);
            var ms = TdldDecoder.Decode(v3);
            var hid = TdldDecoder.Decode(v4);
            Assert.Equal(TdldControllerState.Neutral, ms.FinalState);
            Assert.Equal(TdldControllerState.Neutral, hid.FinalState);
            Assert.Equal(ms.Events.Count, hid.Events.Count);

            // 19 ordinary taps use HID timing. Every explicit duration after them remains in ms.
            int menuStart = 19 * 4;
            Assert.Equal(
                new[] { 100, 75, 100, 75, 200, 50, 4095, 5 },
                hid.Events.Skip(menuStart)
                    .Where(e => e.Command.Kind == TdldEventKind.Wait)
                    .Select(e => e.Command.Value)
            );
            Assert.All(
                hid.Events.Skip(menuStart).Where(e => e.Command.Kind == TdldEventKind.Wait),
                e => Assert.Equal(TdldTimeUnit.Milliseconds, e.Command.TimeUnit)
            );
            Assert.All(
                hid.Events.Take(menuStart).Where(e => e.Command.Kind == TdldEventKind.Wait),
                e => Assert.Equal(TdldTimeUnit.HidPolls, e.Command.TimeUnit)
            );
            for (int i = menuStart; i < ms.Events.Count; i++)
            {
                Assert.Equal(ms.Events[i].Command, hid.Events[i].Command);
                Assert.Equal(ms.Events[i].State, hid.Events[i].State);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(16)]
        [InlineData(17)]
        [InlineData(4096)]
        [InlineData(4097)]
        [InlineData(4098)]
        public void Real_writer_rle_boundaries_decode_to_exact_tap_count(int taps)
        {
            using var route = new TimingSink();
            ISwitchOutput output = route;
            for (int i = 0; i < taps; i++)
                output.Tap(Button.A);

            var document = TdldDecoder.Decode(Export(route, polls: false));
            Assert.Equal(taps * 4, document.Events.Count);
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
        }

        [Fact]
        public void Timing_replay_preserves_held_button_during_movement_and_releases_all_axes()
        {
            using var route = new TimingSink();
            ISwitchOutput output = route;
            output.Press(Button.A);
            output.Tap(DPad.RIGHT);
            output.TapStick(Stick.LX, 255, 100, 75);
            output.ReleaseAll();

            var document = TdldDecoder.Decode(Export(route, polls: true));
            Assert.All(
                document.Events.Take(document.Events.Count - 1),
                e => Assert.Equal(4, e.State.Buttons)
            );
            Assert.Equal(2, document.Events[1].State.DPad);
            Assert.Equal(8, document.Events[3].State.DPad);
            Assert.Equal(255, document.Events[5].State.LX);
            Assert.Equal(128, document.Events[7].State.LX);
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
        }
    }
}
