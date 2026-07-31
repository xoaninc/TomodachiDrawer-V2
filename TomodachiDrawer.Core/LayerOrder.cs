// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

using TomodachiDrawer.Core.ImageProcessing;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.Core
{
    /// <summary>
    /// Chooses the order the colour layers are drawn in.
    /// <para>
    /// Upstream's own TODO in <c>CanvasDrawer.DrawImage</c> asks for exactly this and was never done:
    /// layers came out of <see cref="ColourPalette.BuildFineLayers"/> in whatever order the
    /// distinct-colour walk produced, and the only control was a global reverse — 2 of n! orderings.
    /// </para>
    /// <para>
    /// Switching layers costs two things: navigating the colour picker, and travelling from wherever
    /// the previous layer ended to wherever the next one starts. Both depend on the order, so the
    /// order is a (small) routing problem in its own right.
    /// </para>
    /// <para>
    /// <b>Deliberately not OrTools.</b> At n ≤ 32 greedy + 2-opt is effectively optimal and, more
    /// importantly, <b>deterministic</b> — an OrTools solve is wall-clock budgeted, and putting a
    /// nondeterministic decision *above* the per-layer routing would add noise to every measurement
    /// taken below it.
    /// </para>
    /// </summary>
    public static class LayerOrder
    {
        /// <summary>
        /// The colour picker's resting position before the first selection: hotbar slot 0, which is
        /// black. Mirrors <c>ColourPalette</c>'s own initial <c>_lastGridX/_lastGridY</c>.
        /// </summary>
        private const int InitialGridX = 0;
        private const int InitialGridY = 0;

        /// <summary>
        /// Returns the layers reordered to reduce inter-layer cost. Always a permutation of the input
        /// — every layer appears exactly once, which is the property the caller depends on because
        /// pre-solved routes are keyed by position.
        /// </summary>
        /// <param name="layers">Layers to order. Not mutated.</param>
        /// <param name="cursorX">Canvas cursor position when the layer loop begins.</param>
        /// <param name="cursorY">Canvas cursor position when the layer loop begins.</param>
        public static List<ColourLayer> Optimise(
            IReadOnlyList<ColourLayer> layers,
            int cursorX,
            int cursorY
        )
        {
            int n = layers.Count;
            if (n <= 2)
                return [.. layers]; // nothing to reorder, and n=2 has one ordering up to the start

            var node = new Node[n];
            for (int i = 0; i < n; i++)
                node[i] = Node.For(layers[i]);

            // Two starting points, 2-opt from each, keep the cheaper. The identity seed is what makes
            // "never worse than the order we were given" a guarantee rather than a hope: a greedy
            // construction can land in a local optimum worse than the input, and shipping that would
            // make drawings *longer* while the log claimed an optimisation had happened.
            var greedy = Greedy(node, cursorX, cursorY);
            TwoOpt(node, greedy, cursorX, cursorY);

            var identity = new int[n];
            for (int i = 0; i < n; i++)
                identity[i] = i;
            TwoOpt(node, identity, cursorX, cursorY);

            var order =
                TotalCost(node, greedy, cursorX, cursorY)
                <= TotalCost(node, identity, cursorX, cursorY)
                    ? greedy
                    : identity;

            var result = new List<ColourLayer>(n);
            foreach (int i in order)
                result.Add(layers[i]);
            return result;
        }

        /// <summary>Total cost of an ordering, exposed so tests can assert 2-opt actually improved
        /// it rather than just returning something different.</summary>
        internal static long CostOf(IReadOnlyList<ColourLayer> layers, int cursorX, int cursorY)
        {
            var node = new Node[layers.Count];
            for (int i = 0; i < layers.Count; i++)
                node[i] = Node.For(layers[i]);
            var identity = new int[layers.Count];
            for (int i = 0; i < identity.Length; i++)
                identity[i] = i;
            return TotalCost(node, identity, cursorX, cursorY);
        }

        // ---- the cost model ------------------------------------------------------------------

        /// <summary>
        /// Everything the ordering decision needs about one layer, precomputed. Centroid comes from
        /// the layer's actual points rather than from <c>Extents</c>: for a sparse layer the centre of
        /// the bounding box can sit somewhere the layer has no points at all.
        /// </summary>
        private readonly record struct Node(int CX, int CY, int? GridX, int? GridY)
        {
            public static Node For(ColourLayer l)
            {
                long sx = 0,
                    sy = 0,
                    count = 0;

                foreach (var p in l.FineDetailPoints)
                {
                    sx += p.X;
                    sy += p.Y;
                    count++;
                }
                if (l.StampsBySize != null)
                    foreach (var byS in l.StampsBySize)
                    foreach (var p in byS.Value)
                    {
                        sx += p.X;
                        sy += p.Y;
                        count++;
                    }
                foreach (var p in l.BucketClicks)
                {
                    sx += p.X;
                    sy += p.Y;
                    count++;
                }

                // A layer with no points at all should not exist by this point, but fall back to the
                // bounding-box centre rather than dividing by zero.
                if (count == 0)
                    return new Node(
                        (l.Extents.MinX + l.Extents.MaxX) / 2,
                        (l.Extents.MinY + l.Extents.MaxY) / 2,
                        GridOf(l.Colour).X,
                        GridOf(l.Colour).Y
                    );

                var g = GridOf(l.Colour);
                return new Node((int)(sx / count), (int)(sy / count), g.X, g.Y);
            }

            /// <summary>
            /// Grid coordinates, or null for an Arbitrary colour. Arbitrary selection homes all three
            /// HSV sliders to a corner every time (see <c>ColourPalette.SelectColour</c>), so its
            /// picker cost does not depend on which colour came before — it drops out of the
            /// objective entirely rather than being estimated.
            /// </summary>
            private static (int? X, int? Y) GridOf(PaletteColour c) =>
                c.IsArbitrary || c.GridX is null || c.GridY is null
                    ? (null, null)
                    : (c.GridX, c.GridY);
        }

        /// <summary>
        /// Cost of switching from <paramref name="from"/> to <paramref name="to"/>, in taps.
        /// <para>
        /// Picker term is <b>Manhattan</b>, not Chebyshev, and that is not an oversight:
        /// <c>ColourPalette.SelectColour</c> taps out all of ΔY and then all of ΔX for a grid colour.
        /// Diagonals there are an open question (upstream has a TODO for it and the menu may simply
        /// not accept diagonal input), so this models the cost actually paid rather than the cost
        /// that would be paid if that TODO were done.
        /// </para>
        /// </summary>
        private static long Step(in Node from, in Node to)
        {
            long picker =
                from.GridX is int fx
                && from.GridY is int fy
                && to.GridX is int tx
                && to.GridY is int ty
                    ? Math.Abs(tx - fx) + Math.Abs(ty - fy)
                    : 0;

            long travel = Math.Max(Math.Abs(to.CX - from.CX), Math.Abs(to.CY - from.CY));
            return picker + travel;
        }

        /// <summary>Cost of reaching the first layer from where the cursor and picker actually
        /// are when the layer loop starts.</summary>
        private static long StepFromStart(in Node to, int cursorX, int cursorY)
        {
            long picker =
                to.GridX is int tx && to.GridY is int ty
                    ? Math.Abs(tx - InitialGridX) + Math.Abs(ty - InitialGridY)
                    : 0;

            long travel = Math.Max(Math.Abs(to.CX - cursorX), Math.Abs(to.CY - cursorY));
            return picker + travel;
        }

        private static long TotalCost(Node[] node, int[] order, int cursorX, int cursorY)
        {
            long total = StepFromStart(node[order[0]], cursorX, cursorY);
            for (int k = 0; k + 1 < order.Length; k++)
                total += Step(node[order[k]], node[order[k + 1]]);
            return total;
        }

        // ---- the solver ---------------------------------------------------------------------

        private static int[] Greedy(Node[] node, int cursorX, int cursorY)
        {
            int n = node.Length;
            var order = new int[n];
            var used = new bool[n];

            int best = 0;
            long bestCost = long.MaxValue;
            for (int i = 0; i < n; i++)
            {
                long c = StepFromStart(node[i], cursorX, cursorY);
                // Ties broken by index, so the result does not depend on enumeration order.
                if (c < bestCost)
                {
                    bestCost = c;
                    best = i;
                }
            }
            order[0] = best;
            used[best] = true;

            for (int k = 1; k < n; k++)
            {
                int prev = order[k - 1];
                best = -1;
                bestCost = long.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (used[i])
                        continue;
                    long c = Step(node[prev], node[i]);
                    if (c < bestCost)
                    {
                        bestCost = c;
                        best = i;
                    }
                }
                order[k] = best;
                used[best] = true;
            }

            return order;
        }

        /// <summary>
        /// Standard 2-opt on an open path with a fixed virtual start. Recomputes the whole cost per
        /// candidate rather than an incremental delta: n ≤ 32 makes that ~32k cheap operations per
        /// pass, and being obviously correct is worth more here than being clever.
        /// </summary>
        private static void TwoOpt(Node[] node, int[] order, int cursorX, int cursorY)
        {
            int n = order.Length;
            long current = TotalCost(node, order, cursorX, cursorY);

            // Bounded so a cost model that somehow oscillates cannot spin forever.
            for (int pass = 0; pass < 64; pass++)
            {
                bool improved = false;

                for (int i = 0; i < n - 1; i++)
                for (int j = i + 1; j < n; j++)
                {
                    Array.Reverse(order, i, j - i + 1);
                    long candidate = TotalCost(node, order, cursorX, cursorY);

                    if (candidate < current)
                    {
                        current = candidate;
                        improved = true;
                    }
                    else
                    {
                        Array.Reverse(order, i, j - i + 1); // put it back
                    }
                }

                if (!improved)
                    return;
            }
        }
    }
}
