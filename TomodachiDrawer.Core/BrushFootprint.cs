namespace TomodachiDrawer.Core
{
    /// <summary>
    /// The area a brush covers when stamped. Single definition, shared by the stamp detection in
    /// <see cref="CanvasDrawer"/> and by anything replaying a draw trace — a second copy would be
    /// free to drift, and a renderer that disagrees with the drawer about what a stamp covers
    /// reports bugs that do not exist while hiding ones that do.
    /// </summary>
    public static class BrushFootprint
    {
        /// <summary>
        /// Half-width of a stamp. Integer division, so an even size covers one cell fewer than it
        /// nominally claims — the original code relies on that ("rounds down. which is fine.").
        /// </summary>
        public static int Half(int brushSize) => brushSize / 2;

        /// <summary>
        /// Enumerates the cells a stamp of <paramref name="brushSize"/> centred on
        /// (<paramref name="cx"/>, <paramref name="cy"/>) covers: the square from
        /// <c>-Half</c> to <c>+Half</c> inclusive on both axes. Not bounds-checked — callers know
        /// their canvas.
        /// </summary>
        public static IEnumerable<(int X, int Y)> Offsets(int cx, int cy, int brushSize)
        {
            int half = Half(brushSize);
            for (int dy = -half; dy <= half; dy++)
            for (int dx = -half; dx <= half; dx++)
                yield return (cx + dx, cy + dy);
        }
    }
}
