#!/usr/bin/env python3
"""Build shared adapter projects around immutable sources; patch ONLY an observer copy."""
import argparse
import difflib
import json
import shutil
from pathlib import Path
from xml.sax.saxutils import escape


def observer(source, destination, repo):
    shutil.copytree(source, destination, ignore=shutil.ignore_patterns('bin', 'obj'))
    core = destination / 'TomodachiDrawer.Core'
    (core / 'Tracing').mkdir(exist_ok=True)
    for name in ['IDrawTrace.cs', 'DrawTrace.cs']:
        shutil.copy2(repo / 'TomodachiDrawer.Core/Tracing' / name, core / 'Tracing' / name)
    shutil.copy2(repo / 'TomodachiDrawer.Core/BrushFootprint.cs', core / 'BrushFootprint.cs')
    path = core / 'CanvasDrawer.cs'
    original = path.read_text(encoding='utf-8-sig')
    text = original
    def replace(old, new, count=1):
        nonlocal text
        if text.count(old) != count:
            raise RuntimeError(f'Observer anchor count mismatch ({text.count(old)} != {count}): {old[:90]}')
        text = text.replace(old, new)
    replace('    public partial class CanvasDrawer\n    {', '    public partial class CanvasDrawer\n    {\n        public Tracing.IDrawTrace? ComparisonTrace { get; set; }')
    replace('_toolbar.ClearCanvas();', '_toolbar.ClearCanvas();\n                    ComparisonTrace?.EraseAll();')
    anchor='_realOutput.Tap(Button.A, bucketParanoia ? 100 : 25, bucketParanoia ? 75 : 25);'
    replace(anchor, anchor+'\n                    ComparisonTrace?.BucketFill(new SKColor(bucketColour.Value.R, bucketColour.Value.G, bucketColour.Value.B, 255), _cursorX, _cursorY);')
    replace('chosen.Sink.ReplayTo(_realOutput);','chosen.Sink.ReplayTo(_realOutput);\n                if (ComparisonTrace != null) chosen.Trace.ReplayTo(ComparisonTrace);')
    replace('            string Summary\n        )','            string Summary,\n            Tracing.DrawTrace Trace\n        )')
    replace('            var sink = new TimingSink();','            var sink = new TimingSink();\n            var trace = new Tracing.DrawTrace();')
    replace('                        (sink as ISwitchOutput).Tap(Button.A);','                        (sink as ISwitchOutput).Tap(Button.A);\n                        trace.Stamp(new SKColor(l.Colour.R, l.Colour.G, l.Colour.B, 255), brushSize, point.X, point.Y);')
    replace('                FineDetailSnake(snakeSink, l);','                var snakeTrace = new Tracing.DrawTrace();\n                FineDetailSnake(snakeSink, l, snakeTrace);')
    replace('                FineDetailTsp(tspSink, l, settings.TSPTimeLimit);','                var tspTrace = new Tracing.DrawTrace();\n                FineDetailTsp(tspSink, l, settings.TSPTimeLimit, tspTrace);')
    replace('                    snakeSink.ReplayTo(sink);','                    snakeSink.ReplayTo(sink);\n                    snakeTrace.ReplayTo(trace);')
    replace('                    tspSink.ReplayTo(sink);','                    tspSink.ReplayTo(sink);\n                    tspTrace.ReplayTo(trace);')
    replace('                    (sink as ISwitchOutput).Tap(Button.A, 100, 75);','                    (sink as ISwitchOutput).Tap(Button.A, 100, 75);\n                    trace.BucketFill(new SKColor(l.Colour.R, l.Colour.G, l.Colour.B, 255), click.X, click.Y);')
    replace('                summary.Count > 0 ? string.Join(", ", summary) : "nothing to draw"','                summary.Count > 0 ? string.Join(", ", summary) : "nothing to draw",\n                trace')
    replace('private void FineDetailSnake(ISwitchOutput output, ColourLayer l)','private void FineDetailSnake(ISwitchOutput output, ColourLayer l, Tracing.IDrawTrace trace)')
    replace('                    bool isCurrentPoint = l.FineDetailPoints.Contains(new CanvasPoint(x, y));','                    bool isCurrentPoint = l.FineDetailPoints.Contains(new CanvasPoint(x, y));\n                    if (isCurrentPoint) trace.FineDetail(new SKColor(l.Colour.R, l.Colour.G, l.Colour.B, 255), x, y);')
    replace('private void FineDetailTsp(ISwitchOutput output, ColourLayer l, float timeLimitSeconds)','private void FineDetailTsp(ISwitchOutput output, ColourLayer l, float timeLimitSeconds, Tracing.IDrawTrace trace)')
    replace('                var point = optimizedRoute[idx];','                var point = optimizedRoute[idx];\n                trace.FineDetail(new SKColor(l.Colour.R, l.Colour.G, l.Colour.B, 255), point.X, point.Y);')
    path.write_text(text)
    (destination.parent / 'upstream-observer.patch').write_text(''.join(difflib.unified_diff(original.splitlines(True), text.splitlines(True), fromfile='pristine/CanvasDrawer.cs', tofile='observed/CanvasDrawer.cs')))


def prepare(root, repo):
    observed = root / 'upstream-observed'
    if not observed.exists(): observer(root / 'upstream-clean', observed, repo)
    sources = {'v2-release': root / 'v2-release', 'v2-main': root / 'v2-main', 'upstream-clean': root / 'upstream-clean', 'upstream-observed': observed, 'candidate': repo}
    for name, source in sources.items():
        project_dir = root / 'adapters' / name
        project_dir.mkdir(parents=True, exist_ok=True)
        includes = [repo / 'TomodachiDrawer.Bench' / file for file in ['Program.cs', 'TestImages.cs', 'TraceRenderer.cs', 'MetricsSink.cs', 'TdldDecoder.cs']]
        if name == 'upstream-clean':
            includes.extend(repo / 'TomodachiDrawer.Core' / file for file in ['Tracing/IDrawTrace.cs', 'Tracing/DrawTrace.cs', 'BrushFootprint.cs'])
        compile_items = ''.join(f'<Compile Include="{escape(str(p))}" />' for p in includes)
        constants = 'UPSTREAM' if name.startswith('upstream-') else ''
        project = f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><EnableDefaultCompileItems>false</EnableDefaultCompileItems><DefineConstants>{constants}</DefineConstants></PropertyGroup><ItemGroup>{compile_items}<ProjectReference Include="{escape(str(source / 'TomodachiDrawer.Core/TomodachiDrawer.Core.csproj'))}" /></ItemGroup></Project>'''
        (project_dir / 'Comparison.csproj').write_text(project)
    (root / 'adapter-sources.json').write_text(json.dumps({k: str(v) for k,v in sources.items()}, indent=2))

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--root', type=Path, required=True)
    args=parser.parse_args()
    prepare(args.root.resolve(), Path(__file__).resolve().parents[1])
