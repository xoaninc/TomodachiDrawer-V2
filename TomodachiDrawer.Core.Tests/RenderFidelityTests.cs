using SkiaSharp;
using TomodachiDrawer.Bench;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// End-to-end: generate a drawing, replay its intent trace onto a canvas, and require the result
    /// to be the quantized source image <b>exactly</b>.
    /// <para>
    /// Deliberately not golden-image tests. A golden PNG needs regenerating every time the routing
    /// legitimately changes, and — worse — it enshrines whatever the drawing did on the day it was
    /// captured, bugs included. "The render equals the quantized source" is a stronger claim, needs
    /// no maintenance, and stays meaningful when routes change.
    /// </para>
    /// <para>
    /// What this covers: routing, coverage, layer ordering and bucket-fill geometry. What it cannot:
    /// whether "select colour C" lands on C in-game, since the trace records intent rather than
    /// watching the menus. Hardware testing is still the only sign-off for that.
    /// </para>
    /// </summary>
    public class RenderFidelityTests
    {
        /// <summary>Small and deterministic — this runs on every push, so it has to be quick.</summary>
        private static SKBitmap Blocks(int n, int colours, bool transparentBorder)
        {
            var px = new SKColor[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool border = transparentBorder && (x < 3 || y < 3 || x >= n - 3 || y >= n - 3);
                if (border)
                {
                    px[y * n + x] = SKColors.Transparent;
                    continue;
                }
                int idx = ((x / 4) * 3 + (y / 4) * 5) % colours;
                px[y * n + x] = SKColor.FromHsv(360f * idx / colours, 80f, 60f + 30f * (idx % 2));
            }
            var bmp = new SKBitmap(n, n);
            bmp.Pixels = px;
            return bmp;
        }

        private static SKBitmap QuantizedReference(SKBitmap source, DrawImageSettings settings)
        {
            var palette = new ColourPalette(new DummySink());
            var map = palette.QuantizeImage(source, settings.QuantizerSettings);
            var px = new SKColor[source.Width * source.Height];
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var c = map[x, y];
                px[y * source.Width + x] = c.HasValue
                    ? new SKColor(c.Value.R, c.Value.G, c.Value.B, 255)
                    : SKColors.Transparent;
            }
            var bmp = new SKBitmap(source.Width, source.Height);
            bmp.Pixels = px;
            return bmp;
        }

        // Both solver arms, and both the transparent and fully-opaque cases — the latter is what
        // triggers the erase-all + bucket-fill path on Switch 2.
        [Theory]
        [InlineData(false, true)]
        [InlineData(true, true)]
        [InlineData(false, false)]
        [InlineData(true, false)]
        public async Task Rendered_drawing_equals_the_quantized_source(
            bool parallel,
            bool transparentBorder
        )
        {
            using var image = Blocks(32, 8, transparentBorder);
            var settings = new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings("Arbitrary", 8, false),
                TSPTimeLimit = 0.05f,
            };

            var renderer = new TraceRenderer(image.Width, image.Height);
            var drawer = new CanvasDrawer(
                new DummySink(),
                SwitchVersion.Switch2,
                _ => { },
                parallelSolves: parallel,
                trace: renderer
            );
            drawer.ConnectAndConfirmController();
            await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);

            using var expected = QuantizedReference(image, settings);
            var result = renderer.CompareTo(expected);

            Assert.True(
                result.Considered > 0,
                "nothing was compared — the drawing produced nothing"
            );
            Assert.Equal(0, result.Missing);
            Assert.Equal(0, result.WrongColour);
            Assert.Equal(0, result.Extra);
            Assert.Equal(100.0, result.MatchPercent, 6);
        }

        /// <summary>
        /// The trace must not double-count the fine-detail phase. It dry-runs the snake route and the
        /// TSP route to compare their timings and replays only the faster, so a trace written straight
        /// through would paint every fine-detail cell twice. Overdraw is the signal.
        /// </summary>
        [Fact]
        public async Task Fine_detail_is_not_traced_twice()
        {
            // Transparent border, so no bucket fill runs — any overdraw here would come from the
            // snake/TSP race being recorded twice rather than from legitimate layering.
            using var image = Blocks(32, 4, transparentBorder: true);
            var settings = new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings("Arbitrary", 4, false),
                TSPTimeLimit = 0.05f,
                DisableLargeBrush = true, // no stamps either, so fine detail is all that paints
            };

            var renderer = new TraceRenderer(image.Width, image.Height);
            var drawer = new CanvasDrawer(
                new DummySink(),
                SwitchVersion.Switch2,
                _ => { },
                trace: renderer
            );
            drawer.ConnectAndConfirmController();
            await drawer.DrawImage(image, settings, TestContext.Current.CancellationToken);

            Assert.Equal(0, renderer.Overdraws);
        }
    }
}
