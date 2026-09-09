using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;

namespace TomodachiDrawer.Bench;

/// <summary>Version-neutral comparison adapter. Every repetition is checked and retained.</summary>
internal static class Program
{
    private sealed record Arm(string Name, bool Parallel, int? Threads);

    private static readonly Arm[] Arms =
    [
        new("serial", false, null),
        new("parallel-1thread", true, 1),
        new("parallel-2threads", true, 2),
        new("parallel", true, null),
    ];

    private static async Task<int> Main(string[] args)
    {
        if (Value(args, "--compare-stream") is { } streamA)
        {
            var a = TdldDecoder.Decode(File.ReadAllBytes(streamA));
            var b = TdldDecoder.Decode(File.ReadAllBytes(Arg(args, "--against-stream", "")));
            var difference = TdldDecoder.FindFirstDifference(a, b);
            string json = JsonSerializer.Serialize(
                new
                {
                    Equal = difference is null,
                    ExpectedEvents = a.Events.Count,
                    ActualEvents = b.Events.Count,
                    FirstDifference = difference,
                },
                JsonOptions
            );
            Console.WriteLine(json);
            if (Value(args, "--out") is { } output)
                File.WriteAllText(output, json);
            return difference is null ? 0 : 1;
        }
        float tsp = float.Parse(Arg(args, "--tsp", "1"), CultureInfo.InvariantCulture);
        int repeats = int.Parse(Arg(args, "--repeats", "1"), CultureInfo.InvariantCulture);
        int colours = int.Parse(Arg(args, "--colours", "32"), CultureInfo.InvariantCulture);
        int? threads = Value(args, "--threads") is { } t ? int.Parse(t) : null;
        var version =
            Arg(args, "--switch", "2") == "1" ? SwitchVersion.Switch1 : SwitchVersion.Switch2;
        bool buckets = args.Contains("--buckets");
        bool traceEnabled = !args.Contains("--untraced");
        string root =
            Value(args, "--render-out")
            ?? Path.Combine(Path.GetTempPath(), "tdld-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var chosenArms = threads.HasValue
            ? [new Arm("parallel-" + threads + "threads", true, threads)]
            : Arms;
        if (Value(args, "--arm") is { } armName)
            chosenArms = Arms.Where(a => a.Name == armName).ToArray();
        if (chosenArms.Length == 0 || repeats < 1)
            throw new ArgumentException("Select a valid arm and positive repeat count.");
        bool supportsParallel = typeof(CanvasDrawer)
            .GetConstructors()
            .Any(c => c.GetParameters().Any(p => p.Name == "parallelSolves"));
        if (!supportsParallel)
            chosenArms = [Arms[0]];
        var settings = new DrawImageSettings
        {
            QuantizerSettings = new QuantizerSettings(
                Arg(args, "--quantizer", "Arbitrary"),
                colours,
                false
            ),
            DenoiserName = null,
            TSPTimeLimit = tsp,
            DisableLargeBrush = args.Contains("--no-large-brush"),
            HomeToTopLeft = args.Contains("--home"),
            ReverseColourOrder = args.Contains("--reverse"),
        };
        // Old V2 gates dynamic buckets through ExperimentalFeatures; new V2/upstream have a dedicated flag.
        if (!Set(settings, "EnableBucketFills", buckets))
            settings.EnableExperimentalFeatures = buckets;
        Set(settings, "MinBucketZoneSize", int.Parse(Arg(args, "--min-bucket-size", "36")));
        if (!Set(settings, "EarlyTspExitEnabled", args.Contains("--early-exit")))
            Set(settings, "EarlyExitEnabled", args.Contains("--early-exit"));
        var rows = new List<object>();
        bool failed = false;
        bool merge = !args.Contains("--no-colour-merge");
        // All arms receive a warm process/native solver; warm-up is not a measurement.
        using (var warm = new SKBitmap(32, 32))
        {
            warm.Erase(SKColors.Transparent);
            for (int i = 0; i < 32; i++)
                warm.SetPixel((i * 7) % 30, (i * 11) % 30, SKColors.Red);
            var warmSettings = new DrawImageSettings
            {
                QuantizerSettings = new QuantizerSettings("Arbitrary", 2, false),
                TSPTimeLimit = 0.1f,
                DisableLargeBrush = true,
            };
            var warmDrawer = MakeDrawer(new TimingSink(), version, Arms[0], null, merge, _ => { });
            warmDrawer.ConnectAndConfirmController();
            await warmDrawer.DrawImage(warm, warmSettings);
        }
        var images =
            Value(args, "--image") is { } path ? new[] { LoadImage(path) }
            : Value(args, "--fixture")?.StartsWith("observer-") == true
                ? TestImages.ObserverFixtures().ToArray()
            : TestImages.All().ToArray();
        if (Value(args, "--fixture") is { } fixture)
            images = images.Where(i => i.Name == fixture).ToArray();
        if (images.Length == 0)
            throw new ArgumentException("No fixture matched.");
        foreach (var (name, bitmap) in images)
        {
            using var bmp = bitmap;
            using var expected = QuantizedReference(bmp, settings, merge);
            string sourceHash = PixelHash(bmp),
                expectedHash = PixelHash(expected);
            foreach (var arm in chosenArms)
                for (int repeat = 1; repeat <= repeats; repeat++)
                {
                    string runDir = Path.Combine(root, name, arm.Name, repeat.ToString("D2"));
                    Directory.CreateDirectory(runDir);
                    var logs = new List<string>();
                    var timing = new TimingSink();
                    var renderer = traceEnabled ? new TraceRenderer(bmp.Width, bmp.Height) : null;
                    var drawer = MakeDrawer(timing, version, arm, renderer, merge, logs.Add);
                    var sw = Stopwatch.StartNew();
                    drawer.ConnectAndConfirmController();
                    await drawer.DrawImage(bmp, settings);
                    sw.Stop();
                    var metrics = new MetricsSink();
                    timing.ReplayTo(metrics);
                    DrawComparison? comparison = renderer?.CompareTo(expected);
                    bool passed =
                        comparison is null
                        || (
                            comparison.Value.Missing == 0
                            && comparison.Value.WrongColour == 0
                            && comparison.Value.Extra == 0
                        );
                    failed |= !passed;
                    Save(expected, Path.Combine(runDir, "expected.png"));
                    string? renderedHash = null;
                    if (renderer != null)
                    {
                        using var rendered = renderer.ToBitmap();
                        Save(rendered, Path.Combine(runDir, "rendered.png"));
                        renderedHash = PixelHash(rendered);
                        using var diff = new SKBitmap(bmp.Width, bmp.Height);
                        diff.Pixels = rendered
                            .Pixels.Zip(
                                expected.Pixels,
                                (got, want) => got == want ? SKColors.Transparent : SKColors.Magenta
                            )
                            .ToArray();
                        Save(diff, Path.Combine(runDir, "diff.png"));
                    }
                    string v3 = Path.Combine(runDir, "drawing-v3.tdld");
                    using (var sink = new FileControllerSink(v3))
                        timing.ReplayTo(sink);
                    var decoded = TdldDecoder.Decode(File.ReadAllBytes(v3));
                    bool? bodiesEqual = null;
                    if (
                        typeof(FileControllerSink).GetConstructor([
                            typeof(string),
                            typeof(ushort),
                            typeof(ushort),
                        ]) is
                        { } v4Ctor
                    )
                    {
                        string v4 = Path.Combine(runDir, "drawing-v4.tdld");
                        using (
                            var sink = (FileControllerSink)
                                v4Ctor.Invoke([v4, (ushort)22, (ushort)3])
                        )
                            timing.ReplayTo(sink);
                        bodiesEqual = File.ReadAllBytes(v3)
                            .AsSpan(6)
                            .SequenceEqual(File.ReadAllBytes(v4).AsSpan(16));
                        failed |= bodiesEqual == false;
                    }
                    File.WriteAllLines(Path.Combine(runDir, "generation.log"), logs);
                    var artifactHashes = Directory
                        .GetFiles(runDir)
                        .ToDictionary(
                            file => Path.GetFileName(file)!,
                            file => Hash(File.ReadAllBytes(file))
                        );
                    File.WriteAllText(
                        Path.Combine(runDir, "artifact-hashes.json"),
                        JsonSerializer.Serialize(artifactHashes, JsonOptions)
                    );
                    var row = new
                    {
                        Image = name,
                        Width = bmp.Width,
                        Height = bmp.Height,
                        Arm = arm.Name,
                        Threads = arm.Threads,
                        Repeat = repeat,
                        SourcePixelSha256 = sourceHash,
                        ExpectedPixelSha256 = expectedHash,
                        RenderedPixelSha256 = renderedHash,
                        V3Sha256 = Hash(File.ReadAllBytes(v3)),
                        DecodedEventCount = decoded.Events.Count,
                        FinalControllerState = decoded.FinalState,
                        V3V4BodiesEqual = bodiesEqual,
                        Comparison = comparison,
                        TraceVerified = renderer != null,
                        Passed = passed,
                        metrics.DPadTaps,
                        metrics.PaintActions,
                        metrics.HoldRuns,
                        ColoursSelected = renderer?.DistinctColours,
                        BucketFills = renderer?.BucketFills,
                        EraseAllCount = renderer?.EraseAllCount,
                        DrawSeconds = timing.TotalSeconds,
                        GenerateSeconds = sw.Elapsed.TotalSeconds,
                        ArtifactDirectory = runDir,
                    };
                    rows.Add(row);
                    File.WriteAllText(
                        Path.Combine(runDir, "result.json"),
                        JsonSerializer.Serialize(row, JsonOptions)
                    );
                    Console.WriteLine(
                        $"{name} {arm.Name} repeat={repeat} render={comparison?.ToString() ?? "UNOBSERVED"} gen={sw.Elapsed.TotalSeconds:F3}s draw={timing.TotalSeconds:F3}s"
                    );
                }
        }
        var result = new
        {
            CoreAssembly = typeof(CanvasDrawer).Assembly.FullName,
            Runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            Architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            Cores = Environment.ProcessorCount,
            Args = args,
            Settings = settings,
            SwitchVersion = version.ToString(),
            MergeColours = merge,
            Results = rows,
            Passed = !failed,
        };
        File.WriteAllText(
            Value(args, "--out") ?? Path.Combine(root, "results.json"),
            JsonSerializer.Serialize(result, JsonOptions)
        );
        return failed ? 1 : 0;
    }

    private static CanvasDrawer MakeDrawer(
        TimingSink sink,
        SwitchVersion version,
        Arm arm,
        TraceRenderer? trace,
        bool merge,
        Action<string> log
    )
    {
        var ctor = typeof(CanvasDrawer)
            .GetConstructors()
            .OrderByDescending(c => c.GetParameters().Length)
            .First();
        object?[] values = ctor.GetParameters()
            .Select(p =>
                p.Name switch
                {
                    "parallelSolves" => (object)arm.Parallel,
                    "maxParallelism" => arm.Threads,
                    "trace" => trace,
                    "mergeIdenticalInGameColours" => merge,
                    _ when p.ParameterType.IsInstanceOfType(sink) => sink,
                    _ when p.ParameterType == typeof(SwitchVersion) => version,
                    _ when p.ParameterType == typeof(Action<string>) => log,
                    _ when p.HasDefaultValue => p.DefaultValue,
                    _ => throw new InvalidOperationException(
                        "Unsupported constructor parameter " + p.Name
                    ),
                }
            )
            .ToArray();
        var drawer = (CanvasDrawer)ctor.Invoke(values);
        var observation = typeof(CanvasDrawer).GetProperty("ComparisonTrace");
        observation?.SetValue(drawer, trace);
        if (
            trace != null
            && observation == null
            && !ctor.GetParameters().Any(p => p.Name == "trace")
        )
            throw new InvalidOperationException(
                "This pristine Core cannot render traces; use --untraced or the documented observer copy."
            );
        return drawer;
    }

    private static bool Set(object target, string property, object value)
    {
        var p = target.GetType().GetProperty(property);
        p?.SetValue(target, value);
        return p != null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Hash(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string PixelHash(SKBitmap bmp) =>
        Hash(bmp.Pixels.SelectMany(p => new[] { p.Red, p.Green, p.Blue, p.Alpha }).ToArray());

    private static string? Value(string[] args, string key)
    {
        int i = Array.IndexOf(args, key);
        return i < 0 ? null : args[i + 1];
    }

    private static string Arg(string[] args, string key, string fallback) =>
        Value(args, key) ?? fallback;

    private static void Save(SKBitmap bmp, string path)
    {
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        using var file = File.Create(path);
        data.SaveTo(file);
    }

    private static (string Name, SKBitmap Bitmap) LoadImage(string path)
    {
        var original =
            SKBitmap.Decode(path) ?? throw new InvalidOperationException("Cannot decode " + path);
        if (original.Width <= 256 && original.Height <= 256)
            return (Path.GetFileNameWithoutExtension(path), original);
        float scale = Math.Min(256f / original.Width, 256f / original.Height);
        var resized = original.Resize(
            new SKImageInfo((int)(original.Width * scale), (int)(original.Height * scale)),
            new SKSamplingOptions(SKCubicResampler.CatmullRom)
        );
        original.Dispose();
        return (Path.GetFileNameWithoutExtension(path), resized);
    }

    private static SKBitmap QuantizedReference(
        SKBitmap source,
        DrawImageSettings settings,
        bool merge
    )
    {
        var palette = new ColourPalette(new DummySink());
        var method = typeof(ColourPalette).GetMethods().Single(m => m.Name == "QuantizeImage");
        object?[] args =
            method.GetParameters().Length == 3
                ? [source, settings.QuantizerSettings, merge]
                : [source, settings.QuantizerSettings];
        if (!merge && args.Length == 2)
            throw new InvalidOperationException(
                "This upstream Core cannot disable colour merging."
            );
        var map = (PaletteColour?[,])method.Invoke(palette, args)!;
        var bmp = new SKBitmap(source.Width, source.Height);
        var pixels = new SKColor[source.Width * source.Height];
        for (int y = 0; y < source.Height; y++)
        for (int x = 0; x < source.Width; x++)
        {
            var c = map[x, y];
            pixels[y * source.Width + x] = c.HasValue
                ? new SKColor(c.Value.R, c.Value.G, c.Value.B, 255)
                : SKColors.Transparent;
        }
        bmp.Pixels = pixels;
        return bmp;
    }
}
