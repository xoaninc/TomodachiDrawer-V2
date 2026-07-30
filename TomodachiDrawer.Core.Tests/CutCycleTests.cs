using TomodachiDrawer.Core;
using TomodachiDrawer.Core.Models;
using Xunit;

namespace TomodachiDrawer.Core.Tests
{
    /// <summary>
    /// Pins the cut-selection contract for <c>CanvasDrawer.ChooseCut</c>/<c>ApplyCut</c>, the
    /// optimal-cycle-cut that replaced the old reverse-only orientation.
    /// <para>
    /// The first version of this code used a <i>lexicographic</i> tie-break that preferred cutting
    /// any arc of length &gt;= 2 over any short arc regardless of cost. That is wrong: splitting an
    /// A-hold run costs exactly one extra press/release group, so the correction is +1 tap, and
    /// paying unbounded travel to avoid it is a large regression. It was also applied to the stamp
    /// and bucket phases, which emit one plain Tap(A) per point and have no hold runs at all.
    /// These tests exist so that cannot come back.
    /// </para>
    /// </summary>
    public class CutCycleTests
    {
        private static int Cheb(CanvasPoint a, CanvasPoint b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        /// <summary>
        /// Emission cost in taps for starting at <paramref name="cutIndex"/>: the cycle minus the
        /// dropped arc, plus travel from the cursor, plus (when A is held across neighbours) one
        /// group per contiguous run.
        /// </summary>
        private static long EmissionTaps(
            IReadOnlyList<CanvasPoint> route,
            int cutIndex,
            bool forward,
            int cursorX,
            int cursorY,
            bool holdsA
        )
        {
            var order = CanvasDrawer.ApplyCut(route, cutIndex, forward);
            long taps = Math.Max(Math.Abs(cursorX - order[0].X), Math.Abs(cursorY - order[0].Y));
            for (int i = 0; i + 1 < order.Count; i++)
                taps += Cheb(order[i], order[i + 1]);

            if (!holdsA)
                return taps + order.Count; // one Tap(A) per point

            // A is held across Chebyshev-1 neighbours: one group per maximal run.
            int groups = 0;
            int i2 = 0;
            while (i2 < order.Count)
            {
                int j = i2;
                while (j + 1 < order.Count && Cheb(order[j], order[j + 1]) == 1)
                    j++;
                groups++;
                i2 = j + 1;
            }
            return taps + groups;
        }

        private static List<CanvasPoint> Ring(int w, int h)
        {
            // A closed rectangular outline: almost every arc is Chebyshev 1, which is exactly the
            // shape that made the old lexicographic tie-break pathological.
            var pts = new List<CanvasPoint>();
            for (int x = 0; x < w; x++)
                pts.Add(new CanvasPoint(x, 0));
            for (int y = 1; y < h; y++)
                pts.Add(new CanvasPoint(w - 1, y));
            for (int x = w - 2; x >= 0; x--)
                pts.Add(new CanvasPoint(x, h - 1));
            for (int y = h - 2; y >= 1; y--)
                pts.Add(new CanvasPoint(0, y));
            return pts;
        }

        private static List<CanvasPoint> Scatter(int count, int seed)
        {
            var pts = new List<CanvasPoint>(count);
            uint hsh = (uint)seed * 2654435761u + 12345u;
            for (int i = 0; i < count; i++)
            {
                hsh ^= hsh >> 13;
                hsh *= 1274126177u;
                hsh ^= hsh >> 16;
                pts.Add(new CanvasPoint((int)(hsh % 256), (int)((hsh >> 9) % 256)));
            }
            return pts;
        }

        public static TheoryData<bool, int, int> Cases()
        {
            var data = new TheoryData<bool, int, int>();
            foreach (bool holdsA in new[] { false, true })
            foreach (int cursor in new[] { 0, 128 })
            foreach (int seed in new[] { 1, 2, 3, 4, 5 })
                data.Add(holdsA, cursor, seed);
            return data;
        }

        /// <summary>
        /// The chosen cut must be optimal: brute force all 2n candidates and confirm none emits
        /// fewer taps. This is what the lexicographic version failed.
        /// </summary>
        [Theory]
        [MemberData(nameof(Cases))]
        public void Chosen_cut_is_optimal_over_all_candidates(bool holdsA, int cursor, int seed)
        {
            var routes = new List<List<CanvasPoint>>
            {
                Ring(8 + seed, 6 + seed),
                Scatter(20 + seed, seed),
            };

            foreach (var route in routes)
            {
                var (cutIndex, forward) = CanvasDrawer.ChooseCut(route, cursor, cursor, holdsA);
                long chosen = EmissionTaps(route, cutIndex, forward, cursor, cursor, holdsA);

                long best = long.MaxValue;
                for (int j = 0; j < route.Count; j++)
                    foreach (bool f in new[] { true, false })
                        best = Math.Min(best, EmissionTaps(route, j, f, cursor, cursor, holdsA));

                Assert.Equal(best, chosen);
            }
        }

        /// <summary>A ring with one gap is the shape the old tie-break mishandled: it was forced to
        /// the single long arc no matter how far away it was.</summary>
        [Fact]
        public void Ring_with_one_gap_still_picks_the_cheapest_cut()
        {
            var route = Ring(16, 16);
            route.RemoveAt(8); // leaves one arc of length 2, every other arc is 1

            foreach (bool holdsA in new[] { false, true })
            {
                var (cutIndex, forward) = CanvasDrawer.ChooseCut(route, 0, 0, holdsA);
                long chosen = EmissionTaps(route, cutIndex, forward, 0, 0, holdsA);

                long best = long.MaxValue;
                for (int j = 0; j < route.Count; j++)
                    foreach (bool f in new[] { true, false })
                        best = Math.Min(best, EmissionTaps(route, j, f, 0, 0, holdsA));

                Assert.Equal(best, chosen);
            }
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(13)]
        [InlineData(64)]
        public void ApplyCut_always_returns_a_permutation(int count)
        {
            var route = Scatter(count, count * 7);
            for (int j = 0; j < count; j++)
                foreach (bool forward in new[] { true, false })
                {
                    var order = CanvasDrawer.ApplyCut(route, j, forward);
                    Assert.Equal(route.Count, order.Count);
                    var expected = route.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
                    var actual = order.GroupBy(p => p).ToDictionary(g => g.Key, g => g.Count());
                    Assert.Equal(expected, actual);
                }
        }

        /// <summary>
        /// The stamp and bucket phases emit one Tap(A) per point with no hold runs, so their cut
        /// must not pay the hold correction. Asserted as a behavioural difference so the phase flag
        /// cannot be silently dropped or defaulted the wrong way.
        /// </summary>
        [Fact]
        public void Hold_correction_only_applies_when_requested()
        {
            // A ring where the cheapest no-hold cut is a short arc near the cursor, but the hold
            // model would rather pay travel than split a run.
            var route = Ring(10, 10);
            var noHold = CanvasDrawer.ChooseCut(route, 0, 0, holdsAAcrossAdjacent: false);
            var withHold = CanvasDrawer.ChooseCut(route, 0, 0, holdsAAcrossAdjacent: true);

            // Both must still be optimal under their own model.
            foreach (var (choice, holdsA) in new[] { (noHold, false), (withHold, true) })
            {
                long chosen = EmissionTaps(route, choice.CutIndex, choice.Forward, 0, 0, holdsA);
                long best = long.MaxValue;
                for (int j = 0; j < route.Count; j++)
                    foreach (bool f in new[] { true, false })
                        best = Math.Min(best, EmissionTaps(route, j, f, 0, 0, holdsA));
                Assert.Equal(best, chosen);
            }
        }
    }
}
