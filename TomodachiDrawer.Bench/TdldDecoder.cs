using System.Buffers.Binary;

namespace TomodachiDrawer.Bench
{
    public enum TdldEventKind
    {
        PressButton,
        ReleaseButton,
        PressDPad,
        ReleaseDPad,
        ReleaseAll,
        SetStick,
        Wait,
    }

    public enum TdldTimeUnit
    {
        None,
        Milliseconds,
        HidPolls,
    }

    /// <summary>Arguments use the C# Button/DPad/Stick enum values. Waits store their duration in Value.</summary>
    public readonly record struct TdldCommand(
        TdldEventKind Kind,
        int Argument = 0,
        int Value = 0,
        TdldTimeUnit TimeUnit = TdldTimeUnit.None
    );

    /// <summary>Buttons use the on-wire HID bit layout, not the C# enum order.</summary>
    public readonly record struct TdldControllerState(
        ushort Buttons,
        byte DPad,
        byte LX,
        byte LY,
        byte RX,
        byte RY
    )
    {
        public static TdldControllerState Neutral => new(0, 8, 128, 128, 128, 128);
    }

    public sealed record TdldHeader(
        byte Version,
        int Length,
        ushort HoldPolls,
        ushort ReleasePolls
    );

    public readonly record struct TdldEvent(
        int ByteOffset,
        TdldCommand Command,
        TdldControllerState State
    );

    public sealed record TdldDocument(
        TdldHeader Header,
        IReadOnlyList<TdldEvent> Events,
        TdldControllerState FinalState
    );

    public sealed record TdldDifference(int EventIndex, TdldEvent? Expected, TdldEvent? Actual);

    /// <summary>
    /// Strict validator for exported TDLD files, independent of the writer's opcode constants.
    /// Compact taps expand to press/wait/release/wait, and RLE expands to controller operations.
    /// This models the controller protocol, not the game's menus or pixels.
    /// </summary>
    public static class TdldDecoder
    {
        private static readonly ushort[] ButtonMasks =
        [
            0x0004,
            0x0002,
            0x0008,
            0x0001,
            0x0010,
            0x0020,
            0x0040,
            0x0080,
            0x0100,
            0x0200,
            0x0400,
            0x0800,
            0x1000,
            0x2000,
        ];

        public static TdldDocument Decode(
            ReadOnlySpan<byte> bytes,
            int maxExpandedEvents = 10_000_000
        )
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExpandedEvents);
            if (bytes.Length < 5 || !bytes[..4].SequenceEqual("TDLD"u8))
                throw Invalid(0, "Missing or invalid TDLD header.");

            byte version = bytes[4];
            int headerLength = version switch
            {
                3 => 6,
                4 => 16,
                _ => throw Invalid(4, $"Unsupported TDLD version {version}."),
            };
            if (bytes.Length < headerLength)
                throw Invalid(bytes.Length, "Truncated TDLD header.");

            for (int i = version == 3 ? 5 : 9; i < headerLength; i++)
                if (bytes[i] != 0)
                    throw Invalid(i, "Reserved header padding must be zero.");

            ushort hold =
                version == 4 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[5..7]) : (ushort)0;
            ushort release =
                version == 4 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes[7..9]) : (ushort)0;
            var header = new TdldHeader(version, headerLength, hold, release);
            var events = new List<TdldEvent>();
            var state = TdldControllerState.Neutral;
            byte? lastSingleByteRecord = null;
            int cursor = headerLength;

            while (cursor < bytes.Length)
            {
                int offset = cursor;
                // Both firmwares treat 16 consecutive FF bytes at an opcode boundary as erased flash.
                if (
                    bytes.Length - cursor >= 16
                    && bytes.Slice(cursor, 16).IndexOfAnyExcept((byte)0xFF) < 0
                )
                    throw Invalid(offset, "Erased-flash run is not a valid exported program.");

                byte record = bytes[cursor++];
                int opcode = record >> 4;
                int nibble = record & 15;
                if (opcode == 0)
                {
                    if (record != 0)
                        throw Invalid(offset, "The end marker must be exactly 00.");
                    if (cursor != bytes.Length)
                        throw Invalid(cursor, "Trailing data after the end marker.");
                    return new(header, events.AsReadOnly(), state);
                }

                if (opcode is 6 or 7 or 15)
                {
                    if (cursor >= bytes.Length)
                        throw Invalid(offset, "Truncated two-byte record.");
                    int second = bytes[cursor++];
                    if (opcode == 6)
                    {
                        Add(
                            new(
                                TdldEventKind.Wait,
                                Value: (nibble << 8) | second,
                                TimeUnit: TdldTimeUnit.Milliseconds
                            ),
                            offset
                        );
                    }
                    else if (opcode == 7)
                    {
                        if (nibble > 3)
                            throw Invalid(offset, "Invalid stick axis.");
                        Add(new(TdldEventKind.SetStick, nibble, second), offset);
                    }
                    else
                    {
                        Repeat((nibble << 8) | second, offset);
                    }
                }
                else if (opcode == 14)
                {
                    Repeat(nibble, offset);
                }
                else
                {
                    ExecuteSingle(record, offset);
                    lastSingleByteRecord = record;
                }
            }

            throw Invalid(cursor, "Missing explicit end marker.");

            void Repeat(int count, int offset)
            {
                if (lastSingleByteRecord is not byte last)
                    throw Invalid(offset, "Repeat has no previous single-byte record.");
                if (count == 0)
                    throw Invalid(offset, "Zero-length repeats are not emitted by the writer.");
                // Delays and stick records do not replace the firmware's last single-byte record.
                for (int i = 0; i < count; i++)
                    ExecuteSingle(last, offset);
            }

            void ExecuteSingle(byte record, int offset)
            {
                int opcode = record >> 4;
                int argument = record & 15;
                if (opcode is 1 or 2 or 8 && argument >= ButtonMasks.Length)
                    throw Invalid(offset, "Invalid button.");
                if (opcode is 3 or 4 or 9 && argument > 7)
                    throw Invalid(offset, "Invalid d-pad direction.");
                switch (opcode)
                {
                    case 1:
                        Add(new(TdldEventKind.PressButton, argument), offset);
                        break;
                    case 2:
                        Add(new(TdldEventKind.ReleaseButton, argument), offset);
                        break;
                    case 3:
                        Add(new(TdldEventKind.PressDPad, argument), offset);
                        break;
                    case 4:
                        // The firmware releases the d-pad regardless of the encoded direction.
                        Add(new(TdldEventKind.ReleaseDPad), offset);
                        break;
                    case 5:
                        Add(new(TdldEventKind.ReleaseAll), offset);
                        break;
                    case 8:
                    case 9:
                        Add(
                            new(
                                opcode == 8 ? TdldEventKind.PressButton : TdldEventKind.PressDPad,
                                argument
                            ),
                            offset
                        );
                        TapWait(hold, offset);
                        Add(
                            new(
                                opcode == 8
                                    ? TdldEventKind.ReleaseButton
                                    : TdldEventKind.ReleaseDPad,
                                opcode == 8 ? argument : 0
                            ),
                            offset
                        );
                        TapWait(release, offset);
                        break;
                    default:
                        throw Invalid(offset, $"Unknown opcode {opcode:X}.");
                }
            }

            void TapWait(ushort polls, int offset)
            {
                // tap_half always sends one report, including when a v4 header specifies zero.
                int duration = version == 3 ? 25 : Math.Max(1, (int)polls);
                Add(
                    new(
                        TdldEventKind.Wait,
                        Value: duration,
                        TimeUnit: version == 3 ? TdldTimeUnit.Milliseconds : TdldTimeUnit.HidPolls
                    ),
                    offset
                );
            }

            void Add(TdldCommand command, int offset)
            {
                if (events.Count >= maxExpandedEvents)
                    throw Invalid(offset, $"Expanded event limit of {maxExpandedEvents} exceeded.");
                state = command.Kind switch
                {
                    TdldEventKind.PressButton => state with
                    {
                        Buttons = (ushort)(state.Buttons | ButtonMasks[command.Argument]),
                    },
                    TdldEventKind.ReleaseButton => state with
                    {
                        Buttons = (ushort)(state.Buttons & ~ButtonMasks[command.Argument]),
                    },
                    TdldEventKind.PressDPad => state with { DPad = (byte)command.Argument },
                    TdldEventKind.ReleaseDPad => state with { DPad = 8 },
                    TdldEventKind.ReleaseAll => TdldControllerState.Neutral,
                    TdldEventKind.SetStick => command.Argument switch
                    {
                        0 => state with { LX = (byte)command.Value },
                        1 => state with { LY = (byte)command.Value },
                        2 => state with { RX = (byte)command.Value },
                        _ => state with { RY = (byte)command.Value },
                    },
                    _ => state,
                };
                events.Add(new(offset, command, state));
            }
        }

        /// <summary>Finds the first changed command/state, ignoring compression and source offsets.</summary>
        public static TdldDifference? FindFirstDifference(
            TdldDocument expected,
            TdldDocument actual
        )
        {
            int common = Math.Min(expected.Events.Count, actual.Events.Count);
            for (int i = 0; i < common; i++)
                if (
                    expected.Events[i].Command != actual.Events[i].Command
                    || expected.Events[i].State != actual.Events[i].State
                )
                    return new(i, expected.Events[i], actual.Events[i]);
            return expected.Events.Count == actual.Events.Count
                ? null
                : new(
                    common,
                    common < expected.Events.Count ? expected.Events[common] : null,
                    common < actual.Events.Count ? actual.Events[common] : null
                );
        }

        private static InvalidDataException Invalid(int offset, string message) =>
            new($"TDLD byte {offset} (0x{offset:X}): {message}");
    }
}
