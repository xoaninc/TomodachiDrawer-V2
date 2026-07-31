using TomodachiDrawer.Core;
using TomodachiDrawer.Core.Models;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Pins <c>CanvasDrawer.MoveCost</c> — the arc cost that prices a move in <b>taps</b> rather than
    /// in distance travelled — and the Held-Karp DP that consumes it.
    /// <para>
    /// Upstream tried to express this preference and concluded there was no gain, reasoning that "the
    /// lowest value this can return is 1". That is true and is exactly the trap: with integer costs an
    /// adjacent arc cannot be <i>discounted</i> below 1, so the preference has to be expressed as a
    /// <i>penalty</i> on non-adjacent arcs instead. <see cref="Cost_models_disagree_somewhere"/> is
    /// the test that proves the two objectives are genuinely different rather than a rescaling.
    /// </para>
    /// </summary>
    public class MoveCostTests
    {
        // ---- cost model ----------------------------------------------------------------------

        [Theory]
        // d, expected without hold, expected with hold
        [InlineData(0, 0, 0)]
        [InlineData(1, 1, 1)] // a hold run continues: one DPad tap, no press/release
        [InlineData(2, 2, 3)] // the run breaks: +1
        [InlineData(3, 3, 4)]
        [InlineData(17, 17, 18)]
        public void MoveCost_charges_one_extra_tap_only_when_a_hold_run_breaks(
            int d,
            int expectedPlain,
            int expectedHolding
        )
        {
            var a = new CanvasPoint(0, 0);
            var b = new CanvasPoint(d, 0);

            Assert.Equal(expectedPlain, CanvasDrawer.MoveCost(a, b, holdsAAcrossAdjacent: false));
            Assert.Equal(expectedHolding, CanvasDrawer.MoveCost(a, b, holdsAAcrossAdjacent: true));
        }

        /// <summary>
        /// Diagonals move both axes in one tap, so the penalty keys off Chebyshev and not off either
        /// axis on its own. A diagonal neighbour is a continuing run, not a break.
        /// </summary>
        [Fact]
        public void A_diagonal_neighbour_is_still_adjacent()
        {
            var a = new CanvasPoint(4, 4);
            foreach (var b in new[] { new CanvasPoint(5, 5), new CanvasPoint(3, 5) })
                Assert.Equal(1, CanvasDrawer.MoveCost(a, b, holdsAAcrossAdjacent: true));
        }

        /// <summary>
        /// Never charge the penalty on a phase that does not hold A. Stamps and bucket clicks emit one
        /// plain Tap(A) per point, so for them <c>MoveCost</c> must be exactly Chebyshev — this is the
        /// same phase distinction the first, wrong version of <c>ChooseCut</c> got wrong.
        /// </summary>
        [Fact]
        public void Without_the_hold_flag_MoveCost_is_exactly_Chebyshev()
        {
            foreach (var p in Scatter(24, seed: 3))
            foreach (var q in Scatter(24, seed: 11))
                Assert.Equal(
                    CanvasDrawer.Chebyshev(p, q),
                    CanvasDrawer.MoveCost(p, q, holdsAAcrossAdjacent: false)
                );
        }

        // ---- Held-Karp vs brute force -------------------------------------------------------

        /// <summary>
        /// The DP claims to be exact. At these sizes that claim is checkable outright, so check it
        /// rather than read the recurrence — and check it under <b>both</b> cost models, because
        /// changing an objective is exactly when an "exact" solver quietly stops being exact.
        /// </summary>
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void HeldKarp_matches_the_exhaustive_optimum(bool holdsA)
        {
            foreach (var (name, points) in Instances())
            {
                for (int start = 0; start < points.Length; start++)
                {
                    var dp = CanvasDrawer.HeldKarpFrom(points, start, holdsA);

                    Assert.True(
                        IsPermutationOf(dp, points),
                        $"{name} start={start}: the DP dropped or duplicated a point"
                    );
                    Assert.Equal(points[start], dp[0]);

                    long dpCost = PathCost(dp, holdsA);
                    long best = BruteForceBest(points, start, holdsA);

                    Assert.Equal(best, dpCost);
                }
            }
        }

        /// <summary>
        /// The point of the change: minimising <c>travel</c> and minimising <c>travel + run breaks</c>
        /// are different problems. If no instance ever ordered differently, the penalty would be a
        /// no-op and the commit should be reverted — so this searches deterministically for a
        /// disagreement instead of relying on a hand-picked example.
        /// <para>
        /// It asserts the disagreement in the direction that matters: the route chosen by plain
        /// Chebyshev costs strictly <b>more taps to emit</b> than the one chosen with the penalty.
        /// </para>
        /// </summary>
        [Fact]
        public void Cost_models_disagree_somewhere()
        {
            int disagreements = 0;

            foreach (var (_, points) in Instances())
            {
                for (int start = 0; start < points.Length; start++)
                {
                    var plain = CanvasDrawer.HeldKarpFrom(
                        points,
                        start,
                        holdsAAcrossAdjacent: false
                    );
                    var priced = CanvasDrawer.HeldKarpFrom(
                        points,
                        start,
                        holdsAAcrossAdjacent: true
                    );

                    // Both are compared under the TRUE emission cost — the one with the penalty,
                    // since that is what the hardware actually pays.
                    long plainTaps = PathCost(plain, holdsA: true);
                    long pricedTaps = PathCost(priced, holdsA: true);

                    // The penalised solver must never do worse on the cost it optimises.
                    Assert.True(
                        pricedTaps <= plainTaps,
                        $"start={start}: pricing the hold run produced a worse route "
                            + $"({pricedTaps} vs {plainTaps} taps)"
                    );

                    if (pricedTaps < plainTaps)
                        disagreements++;
                }
            }

            Assert.True(
                disagreements > 0,
                "no instance routed differently under the two cost models — the penalty is a no-op "
                    + "and the objective change should be reverted rather than shipped"
            );
        }

        // ---- helpers -------------------------------------------------------------------------

        /// <summary>
        /// Shapes chosen to exercise the thing under test: runs of adjacent cells separated by gaps,
        /// which is where "keep the hold run" can trade against "travel less". Deterministic — no
        /// <c>Random</c>, so a failure reproduces exactly.
        /// </summary>
        private static IEnumerable<(string Name, CanvasPoint[] Points)> Instances()
        {
            // Two short runs with a gap between them: the classic trade.
            yield return (
                "two runs",
                [
                    new CanvasPoint(0, 0),
                    new CanvasPoint(1, 0),
                    new CanvasPoint(2, 0),
                    new CanvasPoint(7, 3),
                    new CanvasPoint(8, 3),
                    new CanvasPoint(9, 3),
                ]
            );

            // A run plus scattered singletons, so run continuity competes with proximity.
            yield return (
                "run plus singletons",
                [
                    new CanvasPoint(3, 3),
                    new CanvasPoint(4, 4),
                    new CanvasPoint(5, 5),
                    new CanvasPoint(0, 6),
                    new CanvasPoint(9, 1),
                    new CanvasPoint(6, 9),
                ]
            );

            // An L, where the corner can be traversed as one run or split.
            yield return (
                "elbow",
                [
                    new CanvasPoint(0, 0),
                    new CanvasPoint(0, 1),
                    new CanvasPoint(0, 2),
                    new CanvasPoint(1, 2),
                    new CanvasPoint(2, 2),
                    new CanvasPoint(5, 0),
                    new CanvasPoint(6, 0),
                ]
            );

            yield return ("scatter 6", Scatter(6, seed: 1));
            yield return ("scatter 7", Scatter(7, seed: 2));
            yield return ("scatter 8", Scatter(8, seed: 5));
        }

        /// <summary>Deterministic pseudo-scatter — integer arithmetic only, no <c>Random</c>.</summary>
        private static CanvasPoint[] Scatter(int n, int seed)
        {
            var pts = new CanvasPoint[n];
            int h = seed * 2654435761u.GetHashCode();
            for (int i = 0; i < n; i++)
            {
                // A small LCG, written out so the sequence is fixed across runtimes.
                h = unchecked(h * 1103515245 + 12345);
                int x = Math.Abs(h / 65536) % 12;
                h = unchecked(h * 1103515245 + 12345);
                int y = Math.Abs(h / 65536) % 12;
                pts[i] = new CanvasPoint(x, y);
            }
            return pts.Distinct().ToArray();
        }

        private static long PathCost(IReadOnlyList<CanvasPoint> order, bool holdsA)
        {
            long total = 0;
            for (int i = 0; i + 1 < order.Count; i++)
                total += CanvasDrawer.MoveCost(order[i], order[i + 1], holdsA);
            return total;
        }

        /// <summary>
        /// Cheapest open path visiting every point once, starting at <paramref name="start"/>, by
        /// enumerating every permutation of the rest. Exponential on purpose: it is the reference the
        /// DP is checked against, so it must not share any of the DP's reasoning.
        /// </summary>
        private static long BruteForceBest(CanvasPoint[] points, int start, bool holdsA)
        {
            var rest = new List<CanvasPoint>();
            for (int i = 0; i < points.Length; i++)
                if (i != start)
                    rest.Add(points[i]);

            long best = long.MaxValue;
            foreach (var perm in Permutations(rest))
            {
                var order = new List<CanvasPoint>(points.Length) { points[start] };
                order.AddRange(perm);
                best = Math.Min(best, PathCost(order, holdsA));
            }
            return best;
        }

        private static IEnumerable<List<CanvasPoint>> Permutations(List<CanvasPoint> items)
        {
            if (items.Count <= 1)
            {
                yield return new List<CanvasPoint>(items);
                yield break;
            }

            for (int i = 0; i < items.Count; i++)
            {
                var rest = new List<CanvasPoint>(items);
                var head = rest[i];
                rest.RemoveAt(i);
                foreach (var tail in Permutations(rest))
                {
                    var one = new List<CanvasPoint>(items.Count) { head };
                    one.AddRange(tail);
                    yield return one;
                }
            }
        }

        private static bool IsPermutationOf(
            IReadOnlyList<CanvasPoint> route,
            IReadOnlyList<CanvasPoint> points
        ) =>
            route.Count == points.Count
            && route
                .OrderBy(p => p.X)
                .ThenBy(p => p.Y)
                .SequenceEqual(points.OrderBy(p => p.X).ThenBy(p => p.Y));
    }
}
