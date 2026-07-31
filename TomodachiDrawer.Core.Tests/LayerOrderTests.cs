using SkiaSharp;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.ImageProcessing;
using TomodachiDrawer.Core.Models;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Pins <see cref="LayerOrder"/>, which decides the order the colour layers are drawn in —
    /// upstream's own unfinished TODO.
    /// <para>
    /// The dangerous failure here is not a suboptimal order, it is a <b>non-permutation</b>. Pre-solved
    /// routes are keyed by a layer's position in the list, so losing or duplicating a layer hands one
    /// layer's route to another and paints the wrong shape in the wrong colour — and dropping work
    /// makes route cost and estimated draw time both go <i>down</i>, so the headline metrics would
    /// report it as an improvement.
    /// </para>
    /// </summary>
    public class LayerOrderTests
    {
        // ---- the invariants ------------------------------------------------------------------

        [Theory]
        [InlineData(3)]
        [InlineData(8)]
        [InlineData(32)]
        public void Result_is_always_a_permutation_of_the_input(int n)
        {
            var layers = Layers(n, grid: true);

            var ordered = LayerOrder.Optimise(layers, 0, 0);

            Assert.Equal(layers.Count, ordered.Count);
            Assert.Equal(
                layers.Select(l => l.Colour.Name).OrderBy(s => s),
                ordered.Select(l => l.Colour.Name).OrderBy(s => s)
            );
            // Reference equality: the layers themselves must be carried through, not rebuilt. A copy
            // would silently drop the stamp/bucket work that DetectUniformAreas put on them.
            foreach (var l in layers)
                Assert.Contains(l, ordered);
        }

        /// <summary>
        /// The guarantee that makes this safe to enable unconditionally. Greedy construction can land
        /// in a local optimum worse than the order it was handed, and shipping that would make
        /// drawings longer while the log announced an optimisation.
        /// </summary>
        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Never_worse_than_the_order_it_was_given(bool grid)
        {
            foreach (int n in new[] { 3, 5, 9, 17, 32 })
            {
                var layers = Layers(n, grid);

                long before = LayerOrder.CostOf(layers, 0, 0);
                long after = LayerOrder.CostOf(LayerOrder.Optimise(layers, 0, 0), 0, 0);

                Assert.True(
                    after <= before,
                    $"n={n} grid={grid}: ordering made it worse ({before} -> {after} taps)"
                );
            }
        }

        [Fact]
        public void Same_input_gives_the_same_order_every_time()
        {
            var layers = Layers(16, grid: true);

            var first = LayerOrder.Optimise(layers, 40, 90).Select(l => l.Colour.Name).ToList();
            for (int i = 0; i < 5; i++)
                Assert.Equal(
                    first,
                    LayerOrder.Optimise(layers, 40, 90).Select(l => l.Colour.Name).ToList()
                );
        }

        /// <summary>The input must not be mutated — the caller still holds it while building keys.</summary>
        [Fact]
        public void The_input_list_is_left_alone()
        {
            var layers = Layers(12, grid: true);
            var namesBefore = layers.Select(l => l.Colour.Name).ToList();

            LayerOrder.Optimise(layers, 0, 0);

            Assert.Equal(namesBefore, layers.Select(l => l.Colour.Name).ToList());
        }

        // ---- it actually optimises -----------------------------------------------------------

        /// <summary>
        /// Three clusters far apart, handed over in the worst possible order. Any real optimisation
        /// visits each cluster's layers together; the identity order ping-pongs between them.
        /// </summary>
        [Fact]
        public void Interleaved_clusters_get_grouped()
        {
            var layers = new List<ColourLayer>();
            var clusters = new[] { (10, 10), (200, 20), (100, 220) };
            for (int i = 0; i < 3; i++) // interleave: A B C A B C A B C
            for (int c = 0; c < clusters.Length; c++)
                layers.Add(
                    Layer($"c{c}-{i}", clusters[c].Item1 + i, clusters[c].Item2 + i, gx: 0, gy: 0)
                );

            long before = LayerOrder.CostOf(layers, 0, 0);
            var ordered = LayerOrder.Optimise(layers, 0, 0);
            long after = LayerOrder.CostOf(ordered, 0, 0);

            Assert.True(after < before, $"expected an improvement, got {before} -> {after}");

            // Each cluster should now be contiguous in the order.
            var seen = new List<string>();
            foreach (var l in ordered)
            {
                var cluster = l.Colour.Name.Split('-')[0];
                if (seen.Count == 0 || seen[^1] != cluster)
                    seen.Add(cluster);
            }
            Assert.Equal(3, seen.Count); // three runs, not nine alternations
        }

        /// <summary>
        /// For an Arbitrary palette the picker homes its sliders to a corner on every colour, so
        /// picker cost cannot depend on the order. Only canvas travel can — which is why the bench
        /// (Arbitrary) sees a smaller gain than the grid palette would.
        /// </summary>
        [Fact]
        public void Arbitrary_colours_contribute_no_picker_cost()
        {
            // Same centroids, so travel is identical; only the grid coordinates differ.
            var grid = new List<ColourLayer>
            {
                Layer("a", 10, 10, gx: 0, gy: 0),
                Layer("b", 10, 10, gx: 5, gy: 5),
                Layer("c", 10, 10, gx: 1, gy: 1),
            };
            var arbitrary = new List<ColourLayer>
            {
                Layer("a", 10, 10, gx: null, gy: null),
                Layer("b", 10, 10, gx: null, gy: null),
                Layer("c", 10, 10, gx: null, gy: null),
            };

            Assert.True(LayerOrder.CostOf(grid, 0, 0) > LayerOrder.CostOf(arbitrary, 0, 0));
            Assert.Equal(
                Math.Max(10, 10), // just the one hop from the cursor to the shared centroid
                LayerOrder.CostOf(arbitrary, 0, 0)
            );
        }

        [Fact]
        public void Trivial_sizes_are_returned_untouched()
        {
            foreach (int n in new[] { 0, 1, 2 })
            {
                var layers = Layers(n, grid: true);
                Assert.Equal(
                    layers.Select(l => l.Colour.Name),
                    LayerOrder.Optimise(layers, 0, 0).Select(l => l.Colour.Name)
                );
            }
        }

        // ---- helpers -------------------------------------------------------------------------

        private static ColourLayer Layer(string name, int x, int y, int? gx, int? gy)
        {
            var colour = new PaletteColour(
                name,
                1,
                2,
                3,
                gx,
                gy,
                new SKColor(1, 2, 3, 255),
                IsArbitrary: gx is null
            );
            return new ColourLayer
            {
                Colour = colour,
                FineDetailPoints = [new CanvasPoint(x, y)],
                Extents = new LayerExtents(x, x, y, y),
            };
        }

        /// <summary>Deterministic spread of layers — integer arithmetic only, no <c>Random</c>, so a
        /// failure reproduces exactly.</summary>
        private static List<ColourLayer> Layers(int n, bool grid)
        {
            var layers = new List<ColourLayer>(n);
            for (int i = 0; i < n; i++)
            {
                // Coprime strides so the points do not fall into a line.
                int x = (i * 37) % 256;
                int y = (i * 53) % 256;
                layers.Add(
                    grid ? Layer($"g{i}", x, y, i % 6, i / 6) : Layer($"a{i}", x, y, null, null)
                );
            }
            return layers;
        }
    }
}
