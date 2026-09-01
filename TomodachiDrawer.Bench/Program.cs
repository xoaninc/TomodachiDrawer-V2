using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using SkiaSharp;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;

namespace TomodachiDrawer.Bench
{
    /// <summary>
    /// Route-quality and generation-speed baseline. Build and run in <b>Release</b> — wall-clock
    /// numbers from a Debug build are meaningless.
    /// <para>
    /// Three arms, because serial-vs-parallel on its own conflates three different effects: where
    /// the tour gets cut, which greedy seed OR-Tools starts from, and how much CPU each solve gets.
    /// The third arm (parallel code path, one thread) isolates the first two from the third.
    /// </para>
    /// </summary>
    internal static class Program
    {
        private sealed record Arm(string Name, bool Parallel, int? MaxParallelism);

        private static readonly Arm[] Arms =
        [
            new("serial", false, null),
            new("parallel", true, null),
            new("parallel-1thread", true, 1),
        ];

        private sealed record Result(
            string Image,
            int Width,
            int Height,
            string Arm,
            long DPadTaps,
            long PaintActions,
            long HoldRuns,
            int ColoursSelected,
            long StickMoves,
            double DrawSeconds,
            double GenerateSeconds,
            DrawComparison Comparison
        );

        private static async Task<int> Main(string[] args)
        {
            float tspLimit = FloatArg(args, "--tsp", 1.0f);
            int repeats = IntArg(args, "--repeats", 1);
            int colours = IntArg(args, "--colours", 64);
            string quantizer = StringArg(args, "--quantizer", "Arbitrary");
            var version =
                StringArg(args, "--switch", "2") == "1"
                    ? SwitchVersion.Switch1
                    : SwitchVersion.Switch2;
            string? outPath = OptionalArg(args, "--out");
            string? renderDir = OptionalArg(args, "--render-out");

            // A real photo behaves nothing like the synthetic set: its colours come from a
            // quantizer rather than a wheel, and a full-canvas opaque one takes the bucket-fill
            // and erase-all path. Being able to point the harness at the exact file that ran on
            // hardware is what makes a hardware failure checkable offline.
            string? imagePath = OptionalArg(args, "--image");

            // The app turns this on by itself for a full-canvas image, so a run meant to reproduce
            // one has to say so explicitly here.
            bool homeToTopLeft = args.Contains("--home");

            // Upstream's colour merge is on in production and always has been, so every number in
            // Docs/bench/ already includes it and none of them say what it is worth. This turns it
            // off so the difference can be read directly off `layers=` and `draw=`.
            bool mergeColours = !args.Contains("--no-colour-merge");

            Console.WriteLine(
                $"tsp={tspLimit}s repeats={repeats} quantizer={quantizer} colours={colours} "
                    + $"switch={version} cores={Environment.ProcessorCount} "
                    + $"colour-merge={(mergeColours ? "on" : "OFF")} "
                    + $"home={(homeToTopLeft ? "on" : "off")} "
                    + $"config={(IsDebug() ? "DEBUG (wall-clock not meaningful)" : "RELEASE")}"
            );

            // Warm up before measuring anything. The first solve in a process pays for loading and
            // faulting in OR-Tools' native library plus JITting the whole path; measured cold it came in
            // at ~38s against ~6.6s warm for identical work, which would have silently poisoned the
            // first cell of every baseline.
            Console.Write("warming up... ");
            using (var warm = TestImages.All()[0].Bitmap)
                await RunOnce(
                    "warmup",
                    warm,
                    Arms[0],
                    0.1f,
                    quantizer,
                    8,
                    version,
                    null,
                    mergeColours,
                    false
                );
            Console.WriteLine("done");

            var results = new List<Result>();

            var images = imagePath is null ? TestImages.All() : [LoadImage(imagePath)];

            foreach (var (name, bitmap) in images)
            {
                using var bmp = bitmap;
                foreach (var arm in Arms)
                {
                    // Report the best of N: OR-Tools is wall-clock limited, so a run that lost CPU
                    // to something else understates the achievable route, and the noise is one-sided.
                    Result? best = null;
                    for (int i = 0; i < repeats; i++)
                    {
                        var r = await RunOnce(
                            name,
                            bmp,
                            arm,
                            tspLimit,
                            quantizer,
                            colours,
                            version,
                            renderDir,
                            mergeColours,
                            homeToTopLeft
                        );
                        if (best is null || r.DPadTaps < best.DPadTaps)
                            best = r;
                    }
                    results.Add(best!);
                    Console.WriteLine(
                        $"  {name, -10} {arm.Name, -16} dpad={best!.DPadTaps, 8}  paint={best.PaintActions, 7}  "
                            + $"runs={best.HoldRuns, 6}  cols={best.ColoursSelected, 3}  "
                            + $"draw={best.DrawSeconds, 9:F1}s  gen={best.GenerateSeconds, 6:F2}s"
                            + $"\n{new string(' ', 30)}render: {best.Comparison}"
                    );
                }
                ReportDeltas(name, results);
            }

            if (outPath is not null)
            {
                var json = JsonSerializer.Serialize(
                    new
                    {
                        tspLimit,
                        repeats,
                        quantizer,
                        colours,
                        switchVersion = version.ToString(),
                        cores = Environment.ProcessorCount,
                        results,
                    },
                    new JsonSerializerOptions { WriteIndented = true }
                );
                await File.WriteAllTextAsync(outPath, json);
                Console.WriteLine($"\nWrote {outPath}");
            }

            return 0;
        }

        private static async Task<Result> RunOnce(
            string name,
            SKBitmap bmp,
            Arm arm,
            float tspLimit,
            string quantizer,
            int colours,
            SwitchVersion version,
            string? renderDir,
            bool mergeColours,
            bool homeToTopLeft
        )
        {
            var settings = new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings(quantizer, colours, default),
                DenoiserName = null,
                TSPTimeLimit = tspLimit,
                DisableLargeBrush = false,
                EnableExperimentalFeatures = false,
                HomeToTopLeft = homeToTopLeft,
            };

            var timing = new TimingSink();
            var renderer = new TraceRenderer(bmp.Width, bmp.Height);
            var drawer = new CanvasDrawer(
                timing,
                version,
                _ => { },
                parallelSolves: arm.Parallel,
                maxParallelism: arm.MaxParallelism,
                trace: renderer,
                mergeIdenticalInGameColours: mergeColours
            );

            var sw = Stopwatch.StartNew();
            drawer.ConnectAndConfirmController();
            await drawer.DrawImage(bmp, settings);
            sw.Stop();

            // Same trick the app uses to turn a TimingSink into bytes: replay it into another sink.
            var metrics = new MetricsSink();
            timing.ReplayTo(metrics);

            // Compare against the QUANTIZED source, not the raw image: the palette legitimately
            // moves colours, so diffing the original would fail on nearly every pixel and say
            // nothing about whether the drawing is right.
            // Must use the same merge setting, or with --no-colour-merge the reference would be
            // quantized differently from what was drawn and every pixel would read as a mismatch.
            using var expected = QuantizedReference(bmp, settings, mergeColours);
            var comparison = renderer.CompareTo(expected);

            if (renderDir != null && name != "warmup")
            {
                // Eyeballing beats a percentage when something is off: the shape of the wrongness
                // usually says which phase did it.
                Directory.CreateDirectory(renderDir);
                using var rendered = renderer.ToBitmap();
                Save(rendered, Path.Combine(renderDir, $"{name}-{arm.Name}-rendered.png"));
                Save(expected, Path.Combine(renderDir, $"{name}-expected.png"));
            }

            return new Result(
                name,
                bmp.Width,
                bmp.Height,
                arm.Name,
                metrics.DPadTaps,
                metrics.PaintActions,
                metrics.HoldRuns,
                renderer.DistinctColours,
                metrics.StickMoves,
                timing.TotalSeconds,
                sw.Elapsed.TotalSeconds,
                comparison
            );
        }

        /// <summary>
        /// Loads an image the way the app does — same decode, same downscale filter, same 256px
        /// cap. Anything else here would be measuring a different picture than the one the user
        /// drew, which defeats the purpose of pointing the harness at a real file.
        /// </summary>
        private static (string Name, SKBitmap Bitmap) LoadImage(string path)
        {
            var img =
                SKBitmap.Decode(path)
                ?? throw new InvalidOperationException($"Cannot decode {path}");

            if (img.Width > CanvasDrawer.CanvasWidth || img.Height > CanvasDrawer.CanvasHeight)
            {
                float scale = Math.Min(
                    (float)CanvasDrawer.CanvasWidth / img.Width,
                    (float)CanvasDrawer.CanvasHeight / img.Height
                );
                img = img.Resize(
                    new SKImageInfo((int)(img.Width * scale), (int)(img.Height * scale)),
                    new SKSamplingOptions(SKCubicResampler.CatmullRom)
                );
            }

            return (Path.GetFileNameWithoutExtension(path), img);
        }

        private static void Save(SKBitmap bmp, string path)
        {
            using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
            using var file = File.Create(path);
            data.SaveTo(file);
        }

        /// <summary>
        /// The image as the drawer sees it after quantization — one pixel per palette colour, and
        /// transparent where the drawer would not paint.
        /// </summary>
        private static SKBitmap QuantizedReference(
            SKBitmap source,
            DrawImageSettings settings,
            bool mergeColours
        )
        {
            var palette = new ColourPalette(new DummySink());
            var map = palette.QuantizeImage(source, settings.QuantizerSettings, mergeColours);

            var bmp = new SKBitmap(source.Width, source.Height);
            var px = new SKColor[source.Width * source.Height];
            for (int y = 0; y < source.Height; y++)
            for (int x = 0; x < source.Width; x++)
            {
                var c = map[x, y];
                px[y * source.Width + x] = c.HasValue
                    ? new SKColor(c.Value.R, c.Value.G, c.Value.B, 255)
                    : SKColors.Transparent;
            }
            bmp.Pixels = px;
            return bmp;
        }

        /// <summary>
        /// The two numbers that matter per image: how much worse the parallel route is than serial
        /// once CPU contention is removed, and whether coverage changed at all.
        /// </summary>
        private static void ReportDeltas(string image, List<Result> all)
        {
            var forImage = all.Where(r => r.Image == image).ToList();
            var serial = forImage.FirstOrDefault(r => r.Arm == "serial");
            var oneThread = forImage.FirstOrDefault(r => r.Arm == "parallel-1thread");
            if (serial is null || oneThread is null)
                return;

            double pct =
                serial.DPadTaps == 0
                    ? 0
                    : 100.0 * (oneThread.DPadTaps - serial.DPadTaps) / serial.DPadTaps;
            var sb = new StringBuilder();
            sb.Append(
                string.Format(
                    CultureInfo.InvariantCulture,
                    "  -> route cost, parallel-1thread vs serial: {0:+0.00;-0.00;0}%",
                    pct
                )
            );
            var coverage = forImage.Select(r => r.PaintActions).Distinct().ToList();
            if (coverage.Count > 1)
                sb.Append(
                    $"   *** COVERAGE DIFFERS ACROSS ARMS: {string.Join(" / ", forImage.Select(r => $"{r.Arm}={r.PaintActions}"))} ***"
                );
            Console.WriteLine(sb.ToString());
        }

        private static bool IsDebug()
        {
#if DEBUG
            return true;
#else
            return false;
#endif
        }

        private static string? OptionalArg(string[] a, string key)
        {
            int i = Array.IndexOf(a, key);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        private static string StringArg(string[] a, string key, string fallback) =>
            OptionalArg(a, key) ?? fallback;

        private static int IntArg(string[] a, string key, int fallback) =>
            int.TryParse(OptionalArg(a, key), CultureInfo.InvariantCulture, out var v)
                ? v
                : fallback;

        private static float FloatArg(string[] a, string key, float fallback) =>
            float.TryParse(OptionalArg(a, key), CultureInfo.InvariantCulture, out var v)
                ? v
                : fallback;
    }
}
