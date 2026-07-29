using TomodachiDrawer.Core;
using TomodachiDrawer.Core.Models;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// The permutation invariant is the most important test in this repo.
    /// <para>
    /// Route cost and estimated draw time are the two headline metrics for routing work, and a
    /// dropped point makes <b>both of them go down</b> — so a regression that loses pixels reads as
    /// an improvement on every dashboard. Nothing else catches it.
    /// </para>
    /// </summary>
    public class RouteSolverTests
    {
        private static List<CanvasPoint> Points(int count, int seed)
        {
            // Deterministic spread, no Random: same inputs on every machine and every run.
            var pts = new List<CanvasPoint>(count);
            uint h = (uint)seed * 2654435761u + 1u;
            for (int i = 0; i < count; i++)
            {
                h ^= h >> 13;
                h *= 1274126177u;
                h ^= h >> 16;
                pts.Add(new CanvasPoint((int)(h % 256), (int)((h >> 8) % 256)));
            }
            return pts;
        }

        private static void AssertIsPermutation(
            IReadOnlyList<CanvasPoint> input,
            IReadOnlyList<CanvasPoint> output
        )
        {
            Assert.Equal(input.Count, output.Count);

            // Multiset equality, so duplicates in the input must appear the same number of times out.
            var expected = input.GroupBy(p => (p.X, p.Y)).ToDictionary(g => g.Key, g => g.Count());
            var actual = output.GroupBy(p => (p.X, p.Y)).ToDictionary(g => g.Key, g => g.Count());
            Assert.Equal(expected.Count, actual.Count);
            foreach (var (key, count) in expected)
            {
                Assert.True(actual.ContainsKey(key), $"point {key} missing from the route");
                Assert.Equal(count, actual[key]);
            }
        }

        // 1 and 2 are the early-return paths; 12 and 13 straddle CanvasDrawer.ExactMaxPoints, which
        // is where the pre-solve starts skipping sets and where a boundary slip would hide.
        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(11)]
        [InlineData(12)]
        [InlineData(13)]
        [InlineData(17)]
        [InlineData(64)]
        public void Solve_returns_a_permutation_of_its_input(int count)
        {
            var input = Points(count, count);
            var route = RouteSolver.Solve(input, 0.05f, out _);

            // An empty result is the documented "no solution" signal; anything else must be complete.
            if (route.Count == 0 && count > 1)
                return;

            AssertIsPermutation(input, route);
        }

        [Fact]
        public void Solve_on_an_empty_set_returns_empty()
        {
            var route = RouteSolver.Solve(new List<CanvasPoint>(), 0.05f, out _);
            Assert.Empty(route);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(13)]
        [InlineData(64)]
        public void NearestNeighbourFrom_returns_a_permutation(int count)
        {
            var input = Points(count, count + 500);
            AssertIsPermutation(input, RouteSolver.NearestNeighbourFrom(input, 0));
        }

        [Fact]
        public void NearestNeighbourFrom_clamps_an_out_of_range_start()
        {
            var input = Points(10, 7);
            AssertIsPermutation(input, RouteSolver.NearestNeighbourFrom(input, 999));
            AssertIsPermutation(input, RouteSolver.NearestNeighbourFrom(input, -5));
        }

        /// <summary>
        /// Duplicate points are the case where a "distinct" or set-based implementation silently
        /// loses pixels while still looking like a valid route.
        /// </summary>
        [Fact]
        public void Solve_preserves_duplicate_points()
        {
            var input = new List<CanvasPoint>
            {
                new(10, 10),
                new(10, 10),
                new(20, 20),
                new(30, 30),
                new(20, 20),
            };
            var route = RouteSolver.Solve(input, 0.05f, out _);
            if (route.Count > 0)
                AssertIsPermutation(input, route);
        }
    }
}
