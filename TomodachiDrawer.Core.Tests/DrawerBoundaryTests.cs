using SkiaSharp;
using TomodachiDrawer.Bench;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Interfaces;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests;

public class DrawerBoundaryTests
{
    // 0 means the actual serial path; -1 means production's default parallelism.
    public static IEnumerable<object[]> EligibilityCases()
    {
        foreach (int points in new[] { 12, 13 })
        foreach (bool buckets in new[] { false, true })
        foreach (int workers in new[] { 0, 1, 2, -1 })
            yield return [points, buckets, workers];
    }

    public static IEnumerable<object[]> EmptyCases()
    {
        foreach (bool buckets in new[] { false, true })
        foreach (int workers in new[] { 0, 1, 2, -1 })
            yield return [buckets, workers];
    }

    private static DrawImageSettings Settings(bool buckets) =>
        new()
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            TSPTimeLimit = 0.02f,
            DisableLargeBrush = true,
            EnableBucketFills = buckets,
        };

    private static CanvasDrawer Drawer(
        ISwitchOutput output,
        int workers,
        TraceRenderer trace,
        Action<string>? logger = null
    ) =>
        new(
            output,
            SwitchVersion.Switch2,
            logger ?? (_ => { }),
            workers != 0,
            workers > 0 ? workers : null,
            trace
        );

    private static void AssertReference(
        SKBitmap source,
        DrawImageSettings settings,
        TraceRenderer trace
    )
    {
        var palette = new ColourPalette(new DummySink());
        var map = palette.QuantizeImage(source, settings.QuantizerSettings);
        using var expected = new SKBitmap(source.Width, source.Height);
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
            expected.SetPixel(x, y, map[x, y]?.skColor ?? SKColors.Transparent);
        using var rendered = trace.ToBitmap();
        Assert.Equal(expected.Pixels, rendered.Pixels);
    }

    [Theory]
    [MemberData(nameof(EligibilityCases))]
    public async Task Drawer_uses_exact_solver_at_12_and_ortools_at_13(
        int points,
        bool buckets,
        int workers
    )
    {
        using var source = new SKBitmap(20, 14);
        source.Erase(SKColors.Transparent);
        // Transparent gaps prevent both stamps and bucket interiors from changing this phase size.
        for (int i = 0; i < points; i++)
            source.SetPixel(2 + (i % 5) * 3, 2 + (i / 5) * 3, SKColors.Red);
        var settings = Settings(buckets);
        var trace = new TraceRenderer(source.Width, source.Height);
        var drawer = Drawer(new DummySink(), workers, trace);
        int ortoolsCalls = 0;
        drawer.SolverParametersObserver = _ => Interlocked.Increment(ref ortoolsCalls);
        await drawer.DrawImage(source, settings, TestContext.Current.CancellationToken);
        Assert.Equal(points == 12 ? 0 : 1, ortoolsCalls);
        Assert.Equal(0, trace.BucketFills);
        Assert.Equal(0, trace.Overdraws);
        AssertReference(source, settings, trace);
    }

    [Theory]
    [MemberData(nameof(EmptyCases))]
    public async Task Fully_transparent_canvas_has_no_solver_or_paint_operations(
        bool buckets,
        int workers
    )
    {
        // Full-size also exercises the global-prefill transparency guard.
        using var source = new SKBitmap(256, 256);
        source.Erase(SKColors.Transparent);
        var settings = Settings(buckets);
        var trace = new TraceRenderer(256, 256);
        var output = new CountingSink();
        var drawer = Drawer(output, workers, trace);
        int ortoolsCalls = 0;
        drawer.SolverParametersObserver = _ => Interlocked.Increment(ref ortoolsCalls);
        await drawer.DrawImage(source, settings, TestContext.Current.CancellationToken);
        Assert.Equal(0, ortoolsCalls);
        Assert.Equal(0, output.Count);
        Assert.Equal(0, trace.EraseAllCount);
        Assert.Equal(0, trace.BucketFills);
        Assert.Equal(0, trace.DistinctColours);
        AssertReference(source, settings, trace);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(-1)]
    public async Task Cancel_after_candidate_comparison_does_not_flush_chosen_plan(int workers)
    {
        using var source = new SKBitmap(16, 16);
        source.Erase(SKColors.Red);
        var settings = Settings(true);
        settings.MinBucketZoneSize = 1;
        var trace = new TraceRenderer(16, 16);
        var output = new CountingSink();
        using var cancellation = new CancellationTokenSource();
        int? commandsAtComparison = null;
        bool cancelAtComparison = true;
        var drawer = Drawer(
            output,
            workers,
            trace,
            line =>
            {
                if (cancelAtComparison && line.StartsWith("Bucket:", StringComparison.Ordinal))
                {
                    commandsAtComparison = output.Count;
                    cancellation.Cancel();
                }
            }
        );
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            drawer.DrawImage(source, settings, cancellation.Token)
        );
        Assert.NotNull(commandsAtComparison);
        Assert.Equal(commandsAtComparison ?? -1, output.Count);
        Assert.Equal(0, trace.DistinctColours);
        Assert.Equal(0, trace.BucketFills);
        Assert.Equal(0, trace.Overdraws);
        cancelAtComparison = false;
        await drawer.DrawImage(source, settings, TestContext.Current.CancellationToken);
        AssertReference(source, settings, trace);
    }

    private sealed class CountingSink : ISwitchOutput
    {
        public int Count { get; private set; }

        public void Delay(double milliseconds) => Count++;

        public void Press(Button button) => Count++;

        public void Press(DPad direction) => Count++;

        public void Release(Button button) => Count++;

        public void Release(DPad direction) => Count++;

        public void ReleaseAll() => Count++;

        public void SetStick(Stick stick, byte value) => Count++;

        public void Dispose() { }
    }
}
