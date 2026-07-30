using SkiaSharp;

namespace TomodachiDrawer.Core.Tracing
{
    /// <summary>
    /// Records what a drawing <b>intends to paint</b>, alongside the controller inputs that would
    /// achieve it. Optional everywhere — null means no tracing and zero cost.
    /// <para>
    /// The point is to make a drawing checkable without a Switch. Reconstructing the painted result
    /// from the emitted button presses would mean re-implementing the whole Palette House menu state
    /// machine; but <see cref="CanvasDrawer"/> already knows the cursor, the active colour and the
    /// active brush at the moment it paints, so it can just say so.
    /// </para>
    /// <para>
    /// <b>What a trace can and cannot prove.</b> It validates routing, coverage, layer ordering,
    /// homing and bucket-fill geometry — the class of bug that has cost this project the most. It
    /// does <b>not</b> validate that "select colour C" actually lands on C in-game, because it
    /// records the intent rather than watching the menu navigation. So it does not replace testing
    /// on hardware.
    /// </para>
    /// </summary>
    public interface IDrawTrace
    {
        /// <summary>The whole canvas is cleared (the eraser's "erase all").</summary>
        void EraseAll();

        /// <summary>
        /// A brush stamp centred on (<paramref name="x"/>, <paramref name="y"/>). Covers the square
        /// footprint given by <see cref="BrushFootprint.Offsets"/> for this size, not a single pixel.
        /// </summary>
        void Stamp(SKColor colour, int brushSize, int x, int y);

        /// <summary>A single painted cell from the fine-detail pass (brush size 1).</summary>
        void FineDetail(SKColor colour, int x, int y);

        /// <summary>
        /// A bucket fill seeded at (<paramref name="x"/>, <paramref name="y"/>) — floods the
        /// contiguous run of whatever colour is already there.
        /// </summary>
        void BucketFill(SKColor colour, int x, int y);
    }
}
