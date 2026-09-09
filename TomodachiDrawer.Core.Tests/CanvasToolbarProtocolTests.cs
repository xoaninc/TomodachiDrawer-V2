using SkiaSharp;
using TomodachiDrawer.Bench;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests;

public class CanvasToolbarProtocolTests
{
    private readonly record struct Tap(
        int EventIndex,
        TdldEventKind Kind,
        int Argument,
        TdldCommand Hold,
        TdldCommand Release
    );

    private static TdldDocument Decode(TimingSink route, bool polls)
    {
        string path = Path.Combine(Path.GetTempPath(), $"toolbar-protocol-{Guid.NewGuid():N}.tdld");
        try
        {
            using (
                var output = polls
                    ? new FileControllerSink(path, 22, 3)
                    : new FileControllerSink(path)
            )
                route.ReplayTo(output);
            return TdldDecoder.Decode(File.ReadAllBytes(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static List<Tap> Taps(TdldDocument document)
    {
        var taps = new List<Tap>();
        for (int i = 0; i + 3 < document.Events.Count; i++)
        {
            var press = document.Events[i].Command;
            if (press.Kind is not (TdldEventKind.PressButton or TdldEventKind.PressDPad))
                continue;
            var hold = document.Events[i + 1].Command;
            var up = document.Events[i + 2].Command;
            var release = document.Events[i + 3].Command;
            var expectedUp =
                press.Kind == TdldEventKind.PressButton
                    ? TdldEventKind.ReleaseButton
                    : TdldEventKind.ReleaseDPad;
            if (
                hold.Kind == TdldEventKind.Wait
                && up.Kind == expectedUp
                && release.Kind == TdldEventKind.Wait
            )
                taps.Add(new(i, press.Kind, press.Argument, hold, release));
        }
        return taps;
    }

    private static bool MillisecondTap(Tap tap, Button button, int hold, int release) =>
        tap.Kind == TdldEventKind.PressButton
        && tap.Argument == (int)button
        && tap.Hold
            == new TdldCommand(TdldEventKind.Wait, Value: hold, TimeUnit: TdldTimeUnit.Milliseconds)
        && tap.Release
            == new TdldCommand(
                TdldEventKind.Wait,
                Value: release,
                TimeUnit: TdldTimeUnit.Milliseconds
            );

    [Fact]
    public void Snapshot_restore_replays_same_candidate_and_restores_every_toolbar_field()
    {
        using var real = new TimingSink();
        using var setup = new TimingSink();
        var toolbar = new CanvasToolbar(real, SwitchVersion.Switch2);
        var initial = toolbar.Snapshot();
        toolbar.SelectBrush(setup, 7);
        var entry = toolbar.Snapshot();

        using var first = new TimingSink();
        toolbar.SelectBucket(first, paranoid: true);
        toolbar.SelectBrush(first, 19, paranoid: true);
        var end = toolbar.Snapshot();
        Assert.NotEqual(entry, end);
        Assert.NotEqual(entry.LastBrushColumn, end.LastBrushColumn);
        Assert.NotEqual(entry.BucketSubmenuHomed, end.BucketSubmenuHomed);

        toolbar.Restore(entry);
        Assert.Equal(entry, toolbar.Snapshot());
        using var second = new TimingSink();
        toolbar.SelectBucket(second, paranoid: true);
        toolbar.SelectBrush(second, 19, paranoid: true);
        Assert.Equal(end, toolbar.Snapshot());
        Assert.Null(TdldDecoder.FindFirstDifference(Decode(first, false), Decode(second, false)));

        toolbar.Restore(entry);
        using var noChange = new TimingSink();
        toolbar.ReselectLastBrush(noChange, paranoid: true);
        Assert.Empty(Decode(noChange, false).Events);
        toolbar.Restore(initial);
        Assert.Equal(initial, toolbar.Snapshot());
        Assert.Empty(Decode(real, false).Events);
    }

    [Theory]
    [InlineData(SwitchVersion.Switch1)]
    [InlineData(SwitchVersion.Switch2)]
    public void Explicit_sink_overloads_never_write_to_real_output(SwitchVersion version)
    {
        using var real = new TimingSink();
        using var candidate = new TimingSink();
        var toolbar = new CanvasToolbar(real, version);
        toolbar.HomeCanvasToTopLeft(candidate);
        toolbar.ClearCanvas(candidate);
        toolbar.ReselectLastBrush(candidate, paranoid: true); // Unknown-brush fallback.
        toolbar.SelectBrush(candidate, 7, paranoid: true);
        toolbar.SelectBucket(candidate, paranoid: true);
        toolbar.ReselectLastBrush(candidate, paranoid: true); // Known brush.

        Assert.Empty(Decode(real, false).Events);
        var program = Decode(candidate, true);
        Assert.NotEmpty(program.Events);
        Assert.Equal(TdldControllerState.Neutral, program.FinalState);
    }

    [Theory]
    [InlineData(false, false, SwitchVersion.Switch1)]
    [InlineData(false, true, SwitchVersion.Switch1)]
    [InlineData(true, false, SwitchVersion.Switch1)]
    [InlineData(true, true, SwitchVersion.Switch1)]
    [InlineData(false, false, SwitchVersion.Switch2)]
    [InlineData(false, true, SwitchVersion.Switch2)]
    [InlineData(true, false, SwitchVersion.Switch2)]
    [InlineData(true, true, SwitchVersion.Switch2)]
    public void Actual_submenu_taps_have_nonzero_halves_and_preserve_explicit_durations(
        bool paranoid,
        bool polls,
        SwitchVersion version
    )
    {
        using var brush = new TimingSink();
        using var bucket = new TimingSink();
        using var reselect = new TimingSink();
        var toolbar = new CanvasToolbar(new DummySink(), version);
        toolbar.SelectBrush(brush, 7, paranoid);
        toolbar.SelectBucket(bucket, paranoid);
        toolbar.ReselectLastBrush(reselect, paranoid);

        foreach (var route in new[] { brush, bucket, reselect })
        {
            var program = Decode(route, polls);
            var taps = Taps(program);
            Assert.NotEmpty(taps);
            Assert.All(
                taps,
                tap =>
                {
                    Assert.True(tap.Hold.Value > 0);
                    Assert.True(tap.Release.Value > 0);
                }
            );
            // A dropped zero-release bug can preserve a plausible duration total but remove the edge.
            Assert.Equal(
                program.Events.Count(e =>
                    e.Command.Kind is TdldEventKind.PressButton or TdldEventKind.PressDPad
                ),
                taps.Count
            );
            Assert.Equal(TdldControllerState.Neutral, program.FinalState);
            Assert.Contains(
                taps,
                tap => MillisecondTap(tap, Button.A, paranoid ? 100 : 50, paranoid ? 50 : 25)
            );
        }

        Assert.Contains(
            Taps(Decode(brush, polls)),
            tap => MillisecondTap(tap, Button.X, paranoid ? 250 : 175, 50)
        );
        Assert.Contains(
            Taps(Decode(bucket, polls)),
            tap => MillisecondTap(tap, Button.X, paranoid ? 250 : 50, paranoid ? 50 : 25)
        );
        Assert.Contains(
            Taps(Decode(reselect, polls)),
            tap => MillisecondTap(tap, Button.X, paranoid ? 250 : 175, 50)
        );
        if (version == SwitchVersion.Switch1)
            Assert.Contains(
                Taps(Decode(brush, polls)),
                tap => MillisecondTap(tap, Button.A, 75, 200)
            );
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_brush_reselection_preserves_requested_paranoid_timing(bool polls)
    {
        using var route = new TimingSink();
        var toolbar = new CanvasToolbar(new DummySink(), SwitchVersion.Switch2);
        toolbar.ReselectLastBrush(route, paranoid: true);

        var taps = Taps(Decode(route, polls));
        Assert.Contains(taps, tap => MillisecondTap(tap, Button.X, 250, 50));
        Assert.Contains(taps, tap => MillisecondTap(tap, Button.A, 100, 50));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Consecutive_fills_restore_brush_before_travelling_and_reselect_bucket_afterwards(
        bool parallel
    )
    {
        using var image = new SKBitmap(64, 40);
        image.Erase(SKColors.Transparent);
        for (int y = 4; y < 36; y++)
        for (int x = 4; x < 60; x++)
            if (x < 28 || x >= 36)
                image.SetPixel(x, y, SKColors.Red);

        using var route = new TimingSink();
        var trace = new TraceRenderer(64, 40);
        var drawer = new CanvasDrawer(
            route,
            SwitchVersion.Switch2,
            _ => { },
            parallelSolves: parallel,
            maxParallelism: 2,
            trace: trace
        );
        await drawer.DrawImage(
            image,
            new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings("Arbitrary", 2, false),
                TSPTimeLimit = 0.001f,
                DisableLargeBrush = true,
                EnableBucketFills = true,
            },
            TestContext.Current.CancellationToken
        );

        Assert.Equal(2, trace.BucketFills); // Ensure the optimizer really selected two fills.
        var program = Decode(route, polls: true);
        var taps = Taps(program);
        var fills = taps.Where(tap => MillisecondTap(tap, Button.A, 100, 75)).ToList();
        Assert.Equal(2, fills.Count);
        for (int i = 0; i < fills.Count; i++)
        {
            int end = i + 1 < fills.Count ? fills[i + 1].EventIndex : program.Events.Count;
            var afterFill = taps.Where(tap =>
                    tap.EventIndex > fills[i].EventIndex && tap.EventIndex < end
                )
                .ToList();
            var submenu = Assert.Single(afterFill, tap => MillisecondTap(tap, Button.X, 250, 50));
            var brushConfirmed = Assert.Single(
                afterFill,
                tap => MillisecondTap(tap, Button.A, 100, 50)
            );
            Assert.True(submenu.EventIndex < brushConfirmed.EventIndex);
            if (i + 1 < fills.Count)
            {
                var nextToolbar = Assert.Single(
                    afterFill,
                    tap =>
                        tap.EventIndex > brushConfirmed.EventIndex
                        && tap.Kind == TdldEventKind.PressButton
                        && tap.Argument == (int)Button.X
                );
                var travel = program
                    .Events.Skip(brushConfirmed.EventIndex + 4)
                    .Take(nextToolbar.EventIndex - brushConfirmed.EventIndex - 4)
                    .ToList();
                Assert.Contains(travel, e => e.Command.Kind == TdldEventKind.PressDPad);
                Assert.All(
                    travel,
                    e =>
                        Assert.True(
                            e.Command.Kind
                                is TdldEventKind.PressDPad
                                    or TdldEventKind.ReleaseDPad
                                    or TdldEventKind.Wait
                        )
                );
            }
        }
        Assert.Equal(TdldControllerState.Neutral, program.FinalState);
    }
}
