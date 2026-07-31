using SkiaSharp;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.Tracing;

namespace TomodachiDrawer.Bench
{
    /// <summary>
    /// Paints a draw trace onto a canvas, so a generated drawing can be checked against the source
    /// image without a Switch.
    /// <para>
    /// Lives in the bench rather than Core on purpose: nothing that ships needs to render, and Core
    /// staying free of it keeps the trace interface honest — if rendering needed something the trace
    /// does not carry, that is a signal the trace is under-specified, not an excuse to reach into
    /// drawer internals.
    /// </para>
    /// <para>
    /// Unpainted cells are left transparent, so a diff can tell "never drawn" apart from "drawn the
    /// wrong colour" — the two have very different causes.
    /// </para>
    /// </summary>
    public sealed class TraceRenderer : IDrawTrace
    {
        private readonly int _width;
        private readonly int _height;
        private readonly SKColor[] _cells;

        public static readonly SKColor Unpainted = SKColors.Transparent;

        public TraceRenderer(int width, int height)
        {
            _width = width;
            _height = height;
            _cells = new SKColor[width * height];
            Array.Fill(_cells, Unpainted);
        }

        /// <summary>Cells painted more than once. High counts are not wrong on their own (a bucket
        /// fill legitimately covers earlier work) but a spike in the fine-detail phase means the
        /// route is revisiting cells.</summary>
        public long Overdraws { get; private set; }

        private readonly HashSet<uint> _coloursUsed = [];

        /// <summary>
        /// How many distinct colours the drawing actually selected — i.e. the layer count, read off
        /// the trace rather than plumbed out of the drawer. This is what makes upstream's colour merge
        /// measurable: the merge collapses colours reaching the same in-game HSV steps, so its whole
        /// effect is visible here as a smaller number.
        /// </summary>
        public int DistinctColours => _coloursUsed.Count;

        public void EraseAll() => Array.Fill(_cells, Unpainted);

        public void Stamp(SKColor colour, int brushSize, int x, int y)
        {
            // Shared with the drawer's own stamp detection so the two cannot disagree about what a
            // stamp covers.
            foreach (var (px, py) in BrushFootprint.Offsets(x, y, brushSize))
                Set(px, py, colour);
        }

        public void FineDetail(SKColor colour, int x, int y) => Set(x, y, colour);

        public void BucketFill(SKColor colour, int x, int y)
        {
            if (!InBounds(x, y))
                return;

            var target = _cells[Index(x, y)];
            if (target == colour)
                return;

            // Four-way flood of the contiguous run of whatever is already there, which is what the
            // in-game bucket does. Explicit stack rather than recursion: a full 256x256 fill would
            // blow the call stack.
            var stack = new Stack<(int X, int Y)>();
            stack.Push((x, y));
            while (stack.Count > 0)
            {
                var (cx, cy) = stack.Pop();
                if (!InBounds(cx, cy) || _cells[Index(cx, cy)] != target)
                    continue;
                Set(cx, cy, colour);
                stack.Push((cx + 1, cy));
                stack.Push((cx - 1, cy));
                stack.Push((cx, cy + 1));
                stack.Push((cx, cy - 1));
            }
        }

        private void Set(int x, int y, SKColor colour)
        {
            // Counted before the bounds check: selecting a colour is what costs a picker trip, and
            // that happens whether or not the cell it paints lands on the canvas.
            _coloursUsed.Add((uint)colour);

            if (!InBounds(x, y))
                return; // the drawer clamps stamps away from the edges; be forgiving anyway
            int i = Index(x, y);
            if (_cells[i] != Unpainted)
                Overdraws++;
            _cells[i] = colour;
        }

        private bool InBounds(int x, int y) => x >= 0 && y >= 0 && x < _width && y < _height;

        private int Index(int x, int y) => y * _width + x;

        public SKBitmap ToBitmap()
        {
            var bmp = new SKBitmap(_width, _height);
            bmp.Pixels = _cells;
            return bmp;
        }

        /// <summary>
        /// Compares the rendered result against the source image.
        /// <para>
        /// The source is compared <b>after quantization</b> by the caller — comparing against the
        /// raw image would fail on every pixel the palette legitimately moved, which says nothing
        /// about whether the drawing is correct.
        /// </para>
        /// </summary>
        public DrawComparison CompareTo(SKBitmap expected)
        {
            var expectedPixels = expected.Pixels;
            int total = 0,
                matched = 0,
                missing = 0,
                wrongColour = 0,
                extra = 0;

            for (int i = 0; i < _cells.Length && i < expectedPixels.Length; i++)
            {
                var want = expectedPixels[i];
                var got = _cells[i];

                bool wantPainted = want.Alpha >= 128;
                bool gotPainted = got.Alpha >= 128;

                if (!wantPainted && !gotPainted)
                    continue; // transparent in both — not part of the drawing

                total++;
                if (wantPainted && !gotPainted)
                    missing++;
                else if (!wantPainted && gotPainted)
                    extra++;
                else if (SameRgb(want, got))
                    matched++;
                else
                    wrongColour++;
            }

            return new DrawComparison(total, matched, missing, wrongColour, extra, Overdraws);
        }

        private static bool SameRgb(SKColor a, SKColor b) =>
            a.Red == b.Red && a.Green == b.Green && a.Blue == b.Blue;
    }

    /// <param name="Considered">Cells painted in either the source or the render.</param>
    /// <param name="Matched">Same colour in both.</param>
    /// <param name="Missing">In the source but never painted — the serious one, it means dropped points.</param>
    /// <param name="WrongColour">Painted, but not the colour the source wanted.</param>
    /// <param name="Extra">Painted where the source is transparent.</param>
    /// <param name="Overdraws">Cells painted more than once.</param>
    public readonly record struct DrawComparison(
        int Considered,
        int Matched,
        int Missing,
        int WrongColour,
        int Extra,
        long Overdraws
    )
    {
        public double MatchPercent => Considered == 0 ? 100.0 : 100.0 * Matched / Considered;

        public override string ToString() =>
            $"{MatchPercent:F2}% match ({Matched}/{Considered}) "
            + $"missing={Missing} wrongColour={WrongColour} extra={Extra} overdraw={Overdraws}";
    }
}
