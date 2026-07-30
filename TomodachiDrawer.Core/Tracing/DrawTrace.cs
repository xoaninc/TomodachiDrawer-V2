using SkiaSharp;

namespace TomodachiDrawer.Core.Tracing
{
    /// <summary>
    /// Buffers trace events so they can be replayed into another <see cref="IDrawTrace"/> later, or
    /// thrown away.
    /// <para>
    /// This mirrors <see cref="OutputSinks.TimingSink"/> deliberately, and for the same reason: the
    /// fine-detail phase dry-runs <b>both</b> the snake and the TSP route to compare their timings
    /// and only replays the faster one. A trace written straight through would therefore record both
    /// and paint every fine-detail cell twice. Each attempt gets its own buffer and only the winner
    /// is committed.
    /// </para>
    /// </summary>
    public sealed class DrawTrace : IDrawTrace
    {
        private readonly List<Action<IDrawTrace>> _events = [];

        public int Count => _events.Count;

        public void ReplayTo(IDrawTrace target)
        {
            foreach (var e in _events)
                e(target);
        }

        public void EraseAll() => _events.Add(t => t.EraseAll());

        public void Stamp(SKColor colour, int brushSize, int x, int y) =>
            _events.Add(t => t.Stamp(colour, brushSize, x, y));

        public void FineDetail(SKColor colour, int x, int y) =>
            _events.Add(t => t.FineDetail(colour, x, y));

        public void BucketFill(SKColor colour, int x, int y) =>
            _events.Add(t => t.BucketFill(colour, x, y));
    }
}
