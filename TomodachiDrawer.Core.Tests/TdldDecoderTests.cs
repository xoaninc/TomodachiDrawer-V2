using TomodachiDrawer.Bench;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    public class TdldDecoderTests
    {
        private static byte[] V3(string body) =>
            Convert.FromHexString("54444C440300" + body.Replace(" ", ""));

        [Fact]
        public void Literal_v3_tap_has_two_25ms_phases_and_neutral_release()
        {
            var document = TdldDecoder.Decode(V3("80 00"));

            Assert.Equal(new TdldHeader(3, 6, 0, 0), document.Header);
            Assert.Equal(
                new[]
                {
                    new TdldCommand(TdldEventKind.PressButton),
                    new TdldCommand(
                        TdldEventKind.Wait,
                        Value: 25,
                        TimeUnit: TdldTimeUnit.Milliseconds
                    ),
                    new TdldCommand(TdldEventKind.ReleaseButton),
                    new TdldCommand(
                        TdldEventKind.Wait,
                        Value: 25,
                        TimeUnit: TdldTimeUnit.Milliseconds
                    ),
                },
                document.Events.Select(e => e.Command)
            );
            Assert.Equal(4, document.Events[0].State.Buttons);
            Assert.Equal(4, document.Events[1].State.Buttons);
            Assert.Equal(TdldControllerState.Neutral, document.Events[2].State);
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
            Assert.All(document.Events, e => Assert.Equal(6, e.ByteOffset));
        }

        [Fact]
        public void Literal_v4_header_is_little_endian_and_does_not_convert_explicit_delay_to_polls()
        {
            var document = TdldDecoder.Decode(
                Convert.FromHexString(
                    "54444C44" + "04" + "3412" + "5600" + "00000000000000" + "9260FF00"
                )
            );

            Assert.Equal(new TdldHeader(4, 16, 0x1234, 0x56), document.Header);
            Assert.Equal(2, document.Events[0].State.DPad);
            Assert.Equal(
                new TdldCommand(TdldEventKind.Wait, Value: 0x1234, TimeUnit: TdldTimeUnit.HidPolls),
                document.Events[1].Command
            );
            Assert.Equal(
                new TdldCommand(TdldEventKind.Wait, Value: 0x56, TimeUnit: TdldTimeUnit.HidPolls),
                document.Events[3].Command
            );
            Assert.Equal(
                new TdldCommand(
                    TdldEventKind.Wait,
                    Value: 255,
                    TimeUnit: TdldTimeUnit.Milliseconds
                ),
                document.Events[4].Command
            );
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
        }

        [Fact]
        public void Zero_poll_header_matches_firmware_minimum_of_one_report()
        {
            var document = TdldDecoder.Decode(
                Convert.FromHexString("54444C440400000000000000000000008000")
            );
            Assert.All(
                document.Events.Where(e => e.Command.Kind == TdldEventKind.Wait),
                e =>
                {
                    Assert.Equal(1, e.Command.Value);
                    Assert.Equal(TdldTimeUnit.HidPolls, e.Command.TimeUnit);
                }
            );
        }

        [Fact]
        public void Controller_state_tracks_held_buttons_sticks_and_direction_then_releases_everything()
        {
            var document = TdldDecoder.Decode(V3("10 19 33 70FF 7300 6064 20 46 50 00"));
            Assert.Equal(
                new TdldControllerState(0x0204, 3, 255, 128, 128, 0),
                document.Events[5].State
            );
            Assert.Equal(0x0200, document.Events[6].State.Buttons);
            Assert.Equal(8, document.Events[7].State.DPad);
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
        }

        [Theory]
        [InlineData(0, 0x0004)]
        [InlineData(1, 0x0002)]
        [InlineData(2, 0x0008)]
        [InlineData(3, 0x0001)]
        [InlineData(4, 0x0010)]
        [InlineData(5, 0x0020)]
        [InlineData(6, 0x0040)]
        [InlineData(7, 0x0080)]
        [InlineData(8, 0x0100)]
        [InlineData(9, 0x0200)]
        [InlineData(10, 0x0400)]
        [InlineData(11, 0x0800)]
        [InlineData(12, 0x1000)]
        [InlineData(13, 0x2000)]
        public void Button_enum_is_mapped_to_literal_hid_mask(int button, int mask)
        {
            var document = TdldDecoder.Decode(V3($"{0x10 | button:X2} 00"));
            Assert.Equal(mask, document.FinalState.Buttons);
        }

        [Theory]
        [InlineData("80 E1 00", 2)]
        [InlineData("80 EF 00", 16)]
        [InlineData("80 F010 00", 17)]
        [InlineData("80 F100 00", 257)]
        [InlineData("80 FFFF 00", 4096)]
        [InlineData("80 E2 F003 00", 6)]
        public void Rle_count_means_additional_records_and_taps_expand_all_phases(
            string body,
            int taps
        )
        {
            var document = TdldDecoder.Decode(V3(body));
            Assert.Equal(taps * 4, document.Events.Count);
            Assert.Equal(
                taps,
                document.Events.Count(e => e.Command.Kind == TdldEventKind.PressButton)
            );
            Assert.Equal(
                taps,
                document.Events.Count(e => e.Command.Kind == TdldEventKind.ReleaseButton)
            );
            Assert.Equal(TdldControllerState.Neutral, document.FinalState);
        }

        [Fact]
        public void Rle_keeps_previous_single_byte_record_across_delay_and_stick_records()
        {
            var document = TdldDecoder.Decode(V3("10 6064 7080 E2 00"));
            Assert.Equal(5, document.Events.Count);
            Assert.Equal(TdldEventKind.PressButton, document.Events[3].Command.Kind);
            Assert.Equal(TdldEventKind.PressButton, document.Events[4].Command.Kind);
            Assert.Equal(4, document.FinalState.Buttons);
        }

        [Theory]
        [InlineData("")]
        [InlineData("80")]
        [InlineData("60")]
        [InlineData("70")]
        [InlineData("80 F0")]
        [InlineData("E1 00")]
        [InlineData("F001 00")]
        [InlineData("6064 E1 00")]
        [InlineData("80 E0 00")]
        [InlineData("80 F000 00")]
        [InlineData("1E 00")]
        [InlineData("2F 00")]
        [InlineData("8E 00")]
        [InlineData("38 00")]
        [InlineData("48 00")]
        [InlineData("98 00")]
        [InlineData("74FF 00")]
        [InlineData("A0 00")]
        [InlineData("B0 00")]
        [InlineData("C0 00")]
        [InlineData("D0 00")]
        [InlineData("01")]
        [InlineData("00 80")]
        [InlineData("00 00")]
        public void Malformed_or_truncated_body_is_rejected(string body)
        {
            var exception = Assert.Throws<InvalidDataException>(() => TdldDecoder.Decode(V3(body)));
            Assert.StartsWith("TDLD byte ", exception.Message);
        }

        [Theory]
        [InlineData("")]
        [InlineData("54444C44")]
        [InlineData("58444C44030000")]
        [InlineData("54444C44050000")]
        [InlineData("54444C44030100")]
        [InlineData("54444C4404160003000100000000000000")]
        public void Invalid_header_is_rejected(string hex)
        {
            Assert.Throws<InvalidDataException>(() =>
                TdldDecoder.Decode(Convert.FromHexString(hex))
            );
        }

        [Theory]
        [InlineData("54444C440300", 6)]
        [InlineData("54444C44041600030000000000000000", 16)]
        public void Every_truncated_header_prefix_is_rejected(string headerHex, int headerLength)
        {
            byte[] header = Convert.FromHexString(headerHex);
            Assert.Equal(headerLength, header.Length);
            for (int length = 0; length < header.Length; length++)
            {
                byte[] prefix = header[..length];
                Assert.Throws<InvalidDataException>(() => TdldDecoder.Decode(prefix));
            }
        }

        [Fact]
        public void Erased_flash_and_excessive_expansion_are_rejected()
        {
            Assert.Throws<InvalidDataException>(() =>
                TdldDecoder.Decode(V3("80" + new string('F', 32) + "00"))
            );
            Assert.Throws<InvalidDataException>(() =>
                TdldDecoder.Decode(V3("80 FFFF 00"), maxExpandedEvents: 100)
            );
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                TdldDecoder.Decode(V3("00"), maxExpandedEvents: 0)
            );
        }

        [Fact]
        public void Normalized_comparison_ignores_compression_offsets_and_reports_first_real_difference()
        {
            var compressed = TdldDecoder.Decode(V3("80 E2 00"));
            var expanded = TdldDecoder.Decode(V3("80 80 80 00"));
            Assert.Null(TdldDecoder.FindFirstDifference(compressed, expanded));

            var changed = TdldDecoder.Decode(V3("80 80 81 00"));
            var difference = Assert.IsType<TdldDifference>(
                TdldDecoder.FindFirstDifference(compressed, changed)
            );
            Assert.Equal(8, difference.EventIndex);
            Assert.Equal(7, difference.Expected!.Value.ByteOffset);
            Assert.Equal(8, difference.Actual!.Value.ByteOffset);

            var shortened = TdldDecoder.Decode(V3("80 00"));
            difference = Assert.IsType<TdldDifference>(
                TdldDecoder.FindFirstDifference(compressed, shortened)
            );
            Assert.Equal(4, difference.EventIndex);
            Assert.Null(difference.Actual);
        }
    }
}
