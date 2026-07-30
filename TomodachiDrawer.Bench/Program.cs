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
            long StickMoves,
            double DrawSeconds,
            double GenerateSeconds
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

            Console.WriteLine(
                $"tsp={tspLimit}s repeats={repeats} quantizer={quantizer} colours={colours} "
                    + $"switch={version} cores={Environment.ProcessorCount} "
                    + $"config={(IsDebug() ? "DEBUG (wall-clock not meaningful)" : "RELEASE")}"
            );

            // Warm up before measuring anything. The first solve in a process pays for loading and
            // faulting in OR-Tools' native library plus JITting the whole path; measured cold it came in
            // at ~38s against ~6.6s warm for identical work, which would have silently poisoned the
            // first cell of every baseline.
            Console.Write("warming up... ");
            using (var warm = TestImages.All()[0].Bitmap)
                await RunOnce("warmup", warm, Arms[0], 0.1f, quantizer, 8, version);
            Console.WriteLine("done");

            var results = new List<Result>();

            foreach (var (name, bitmap) in TestImages.All())
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
                            version
                        );
                        if (best is null || r.DPadTaps < best.DPadTaps)
                            best = r;
                    }
                    results.Add(best!);
                    Console.WriteLine(
                        $"  {name, -10} {arm.Name, -16} dpad={best!.DPadTaps, 8}  paint={best.PaintActions, 7}  "
                            + $"draw={best.DrawSeconds, 9:F1}s  gen={best.GenerateSeconds, 6:F2}s"
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
            SwitchVersion version
        )
        {
            var settings = new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings(quantizer, colours, default),
                DenoiserName = null,
                TSPTimeLimit = tspLimit,
                DisableLargeBrush = false,
                EnableExperimentalFeatures = false,
                HomeToTopLeft = false,
            };

            var timing = new TimingSink();
            var drawer = new CanvasDrawer(
                timing,
                version,
                _ => { },
                parallelSolves: arm.Parallel,
                maxParallelism: arm.MaxParallelism
            );

            var sw = Stopwatch.StartNew();
            drawer.ConnectAndConfirmController();
            await drawer.DrawImage(bmp, settings);
            sw.Stop();

            // Same trick the app uses to turn a TimingSink into bytes: replay it into another sink.
            var metrics = new MetricsSink();
            timing.ReplayTo(metrics);

            return new Result(
                name,
                bmp.Width,
                bmp.Height,
                arm.Name,
                metrics.DPadTaps,
                metrics.PaintActions,
                metrics.StickMoves,
                timing.TotalSeconds,
                sw.Elapsed.TotalSeconds
            );
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
