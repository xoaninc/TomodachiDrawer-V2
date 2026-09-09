using SkiaSharp;
using TomodachiDrawer.Bench;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests;

public class BucketPlanningTests
{
    [Theory]
    [InlineData(false, 1, false)]
    [InlineData(true, 1, false)]
    [InlineData(true, 4, false)]
    [InlineData(true, 4, true)]
    public async Task Nested_walls_render_exactly_and_only_winning_plans_are_traced(
        bool parallel,
        int threads,
        bool reverse
    )
    {
        // Several nested touching layers, transparent outer canvas, large interiors and thin walls.
        // Buckets should win here; rejected plain traces would register overdraw.
        using var image = new SKBitmap(64, 64);
        var colours = new[] { SKColors.Transparent, SKColors.Red, SKColors.Blue, SKColors.Lime };
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
        {
            int rim = Math.Min(Math.Min(x, y), Math.Min(63 - x, 63 - y));
            image.SetPixel(
                x,
                y,
                colours[
                    rim < 2 ? 0
                    : rim < 5 ? 1
                    : rim < 8 ? 2
                    : 3
                ]
            );
        }
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new QuantizerSettings("Arbitrary", 4, false),
            TSPTimeLimit = 0.02f,
            DisableLargeBrush = true,
            EnableBucketFills = true,
            ReverseColourOrder = reverse,
            EarlyTspExitEnabled = true,
            EarlyTspExitRateCoefficient = 0.2,
            EarlyTspExitSolutionsDistance = 7,
        };
        var palette = new ColourPalette(new DummySink());
        var map = palette.QuantizeImage(image, settings.QuantizerSettings);
        using var expected = new SKBitmap(64, 64);
        for (int y = 0; y < 64; y++)
        for (int x = 0; x < 64; x++)
            expected.SetPixel(x, y, map[x, y]?.skColor ?? SKColors.Transparent);
        var trace = new TraceRenderer(64, 64);
        var drawer = new CanvasDrawer(
            new DummySink(),
            SwitchVersion.Switch2,
            _ => { },
            parallel,
            threads,
            trace
        );
        var observed =
            new System.Collections.Concurrent.ConcurrentBag<Google.OrTools.ConstraintSolver.RoutingSearchParameters>();
        drawer.SolverParametersObserver = parameters => observed.Add(parameters.Clone());
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
        Assert.NotEmpty(observed);
        Assert.All(
            observed,
            parameters =>
            {
                Assert.NotNull(parameters.ImprovementLimitParameters);
                Assert.Equal(0.2, parameters.ImprovementLimitParameters.ImprovementRateCoefficient);
                Assert.Equal(
                    7,
                    parameters.ImprovementLimitParameters.ImprovementRateSolutionsDistance
                );
            }
        );
        var result = trace.CompareTo(expected);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.WrongColour);
        Assert.Equal(0, result.Extra);
        Assert.Equal(0, trace.Overdraws);
    }

    [Fact]
    public async Task Rejected_bucket_candidate_emits_no_fill_or_duplicate_trace()
    {
        using var image = new SKBitmap(16, 16);
        image.Erase(SKColors.Red);
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            TSPTimeLimit = 0.01f,
            EnableBucketFills = true,
            MinBucketZoneSize = 1,
        };
        var trace = new TraceRenderer(16, 16);
        var logs = new List<string>();
        var drawer = new CanvasDrawer(
            new DummySink(),
            SwitchVersion.Switch2,
            logs.Add,
            true,
            2,
            trace
        );
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
        Assert.Contains(logs, line => line.StartsWith("Bucket:"));
        Assert.Equal(0, trace.BucketFills);
        Assert.Equal(0, trace.Overdraws);
    }

    [Fact]
    public void Minimum_zone_size_changes_candidate_geometry_without_mutating_source()
    {
        using var image = new SKBitmap(16, 16);
        image.Erase(SKColors.Red);
        var palette = new ColourPalette(new DummySink());
        var original = palette.BuildFineLayers(
            palette.QuantizeImage(image, new QuantizerSettings("Arbitrary", 2, false))
        )[0];
        var smallMinimum = original.Clone();
        var largeMinimum = original.Clone();
        Assert.True(CanvasDrawer.DetectBucketZones(smallMinimum, 16, 16, new bool[16, 16], 1) > 0);
        Assert.Equal(
            0,
            CanvasDrawer.DetectBucketZones(largeMinimum, 16, 16, new bool[16, 16], int.MaxValue)
        );
        Assert.Equal(256, original.FineDetailPoints.Count);
        Assert.Equal(256, largeMinimum.FineDetailPoints.Count);
        Assert.True(smallMinimum.FineDetailPoints.Count < original.FineDetailPoints.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Solver_failure_keeps_all_pixels_and_reports_fallback(bool parallel)
    {
        using var image = new SKBitmap(32, 32);
        image.Erase(SKColors.Red);
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            DisableLargeBrush = true,
            TSPTimeLimit = 0.01f,
        };
        var trace = new TraceRenderer(32, 32);
        var logs = new System.Collections.Concurrent.ConcurrentBag<string>();
        var drawer = new CanvasDrawer(
            new DummySink(),
            SwitchVersion.Switch2,
            logs.Add,
            parallel,
            2,
            trace
        );
        drawer.SolverParametersObserver = _ =>
            throw new InvalidOperationException("injected solver failure");
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
        Assert.Contains(
            logs,
            line => line.Contains("injected solver failure") && line.Contains("fallback")
        );
        var palette = new ColourPalette(new DummySink());
        var colour = palette.QuantizeImage(image, settings.QuantizerSettings)[0, 0]!.Value.skColor;
        using var expected = new SKBitmap(32, 32);
        expected.Erase(colour);
        var result = trace.CompareTo(expected);
        Assert.Equal(0, result.Missing);
        Assert.Equal(0, result.WrongColour);
        Assert.Equal(0, result.Extra);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_at_solver_boundary_is_not_converted_to_fallback(bool parallel)
    {
        using var image = new SKBitmap(32, 32);
        image.Erase(SKColors.Red);
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            DisableLargeBrush = true,
            TSPTimeLimit = 0.01f,
        };
        using var cancellation = new CancellationTokenSource();
        var trace = new TraceRenderer(32, 32);
        var drawer = new CanvasDrawer(
            new DummySink(),
            SwitchVersion.Switch2,
            _ => { },
            parallel,
            2,
            trace
        );
        drawer.SolverParametersObserver = _ =>
        {
            cancellation.Cancel();
            cancellation.Token.ThrowIfCancellationRequested();
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            drawer.DrawImage(image, settings, cancellation.Token)
        );
        Assert.Equal(0, trace.Overdraws);
        Assert.Equal(0, trace.BucketFills);
        drawer.SolverParametersObserver = null;
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void Cloning_geometry_does_not_share_mutable_collections()
    {
        using var image = new SKBitmap(16, 16);
        image.Erase(SKColors.Red);
        var palette = new ColourPalette(new DummySink());
        var layer = palette.BuildFineLayers(
            palette.QuantizeImage(image, new QuantizerSettings("Arbitrary", 2, false))
        )[0];
        layer.StampsBySize[3] = [new CanvasPoint(4, 4)];
        layer.BucketClicks.Add(new CanvasPoint(6, 6));
        var clone = layer.Clone();
        clone.FineDetailPoints.Clear();
        clone.StampsBySize[3].Clear();
        clone.BucketClicks.Clear();
        Assert.Equal(256, layer.FineDetailPoints.Count);
        Assert.Single(layer.StampsBySize[3]);
        Assert.Single(layer.BucketClicks);
    }

    [Fact]
    public void Existing_experimental_flag_does_not_enable_bucket_fills()
    {
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            EnableExperimentalFeatures = true,
        };
        Assert.False(settings.EnableBucketFills);
    }

    [Fact]
    public async Task Cancellation_during_preparation_propagates_and_next_generation_works()
    {
        using var image = new SKBitmap(32, 32);
        image.Erase(SKColors.Red);
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new("Arbitrary", 2, false),
            EnableBucketFills = true,
            TSPTimeLimit = 0.01f,
        };
        var drawer = new CanvasDrawer(
            new DummySink(),
            SwitchVersion.Switch2,
            _ => { },
            parallelSolves: true
        );
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            drawer.DrawImage(image, settings, cts.Token)
        );
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
    }
}
