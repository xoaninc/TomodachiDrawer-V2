using SkiaSharp;
using TomodachiDrawer.Bench;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests;

public class BucketRenderFidelityTests
{
    [Theory]
    [InlineData(0, false, false, SwitchVersion.Switch1)]
    [InlineData(1, true, false, SwitchVersion.Switch2)]
    [InlineData(2, false, true, SwitchVersion.Switch2)]
    [InlineData(3, true, true, SwitchVersion.Switch1)]
    public async Task Candidate_strategies_render_exactly_across_thread_counts(
        int shape,
        bool reverse,
        bool disableBrush,
        SwitchVersion version
    )
    {
        using var image = Fixture(shape);
        foreach (bool buckets in new[] { false, true })
        foreach (int threads in new[] { 0, 1, 2, -1 })
            for (int repeat = 0; repeat < 10; repeat++)
            {
                var settings = Settings(buckets);
                settings.ReverseColourOrder = reverse;
                settings.DisableLargeBrush = disableBrush;
                var trace = new TraceRenderer(image.Width, image.Height);
                var drawer = new CanvasDrawer(
                    new DummySink(),
                    version,
                    _ => { },
                    parallelSolves: threads != 0,
                    maxParallelism: threads > 0 ? threads : null,
                    trace: trace
                );
                drawer.ConnectAndConfirmController();
                await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
                using var expected = Reference(image, settings);
                var result = trace.CompareTo(expected);
                Assert.True(
                    result.Missing == 0 && result.WrongColour == 0 && result.Extra == 0,
                    $"shape={shape} buckets={buckets} threads={threads} repeat={repeat}: {result}"
                );
            }
    }

    [Theory]
    [InlineData(SwitchVersion.Switch1)]
    [InlineData(SwitchVersion.Switch2)]
    public async Task Full_canvas_really_exercises_erase_and_background_bucket(
        SwitchVersion version
    )
    {
        using var image = new SKBitmap(256, 256);
        image.Erase(SKColors.CornflowerBlue);
        var settings = Settings(false);
        settings.HomeToTopLeft = true;
        var trace = new TraceRenderer(256, 256);
        var drawer = new CanvasDrawer(
            new DummySink(),
            version,
            _ => { },
            parallelSolves: true,
            trace: trace
        );
        drawer.ConnectAndConfirmController();
        await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);
        using var expected = Reference(image, settings);
        Assert.Equal(1, trace.EraseAllCount);
        Assert.Equal(1, trace.BucketFills);
        Assert.Equal(100.0, trace.CompareTo(expected).MatchPercent);
    }

    [Fact]
    public void Renderer_rejects_bad_dimensions_and_invalid_coordinates()
    {
        var renderer = new TraceRenderer(8, 8);
        Assert.Throws<InvalidOperationException>(() => renderer.FineDetail(SKColors.Red, 8, 0));
        Assert.Throws<InvalidOperationException>(() => renderer.BucketFill(SKColors.Red, -1, 0));
        using var wrong = new SKBitmap(7, 8);
        Assert.Throws<ArgumentException>(() => renderer.CompareTo(wrong));
    }

    private static DrawImageSettings Settings(bool buckets) =>
        new()
        {
            QuantizerSettings = new QuantizerSettings("Arbitrary", 4, false),
            TSPTimeLimit = 0.001f,
            EnableBucketFills = buckets,
            MinBucketZoneSize = 36,
        };

    private static SKBitmap Fixture(int shape)
    {
        const int n = 24;
        var image = new SKBitmap(n, n);
        var pixels = new SKColor[n * n];
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            // Shared borders, nested holes, open one-pixel boundaries and diagonal contact.
            pixels[y * n + x] = shape switch
            {
                0 => x < 12 ? SKColors.Red
                : y < 12 ? SKColors.Blue
                : SKColors.Green,
                1 => x < 2 || y < 2 || x > 21 || y > 21 ? SKColors.Transparent
                : x >= 8 && x <= 15 && y >= 8 && y <= 15 ? SKColors.Blue
                : SKColors.Red,
                2 => (x == 5 || x == 18 || y == 5 || y == 18) && !(x == 11 && y == 5)
                    ? SKColors.Blue
                    : SKColors.Red,
                _ => x == y || x == y + 1 ? SKColors.Blue
                : (x / 6 + y / 6) % 2 == 0 ? SKColors.Red
                : SKColors.Green,
            };
        }
        image.Pixels = pixels;
        return image;
    }

    private static SKBitmap Reference(SKBitmap image, DrawImageSettings settings)
    {
        var map = new ColourPalette(new DummySink()).QuantizeImage(
            image,
            settings.QuantizerSettings
        );
        var result = new SKBitmap(image.Width, image.Height);
        var pixels = new SKColor[image.Width * image.Height];
        for (int y = 0; y < image.Height; y++)
        for (int x = 0; x < image.Width; x++)
        {
            var c = map[x, y];
            pixels[y * image.Width + x] = c.HasValue
                ? new SKColor(c.Value.R, c.Value.G, c.Value.B, 255)
                : SKColors.Transparent;
        }
        result.Pixels = pixels;
        return result;
    }
}
