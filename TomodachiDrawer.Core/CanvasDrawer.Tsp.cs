using System.Diagnostics;
using Google.OrTools.ConstraintSolver;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.Core
{
    // All TSP routing logic, recommended values, and fallbacks and shortcuts.
    public partial class CanvasDrawer
    {
        /// <summary>
        /// Point counts at or below this are solved exactly by Held-Karp instead of handed to
        /// OrTools: optimal, and it dodges the improvement limit being flaky on tiny instances.
        /// <para>
        /// Kept at 12 rather than upstream's 16 deliberately. The DP allocates two
        /// <c>int[1 &lt;&lt; n, n]</c> arrays, so n=16 is 8 MB of large-object heap and ~8.4M inner
        /// iterations per call, against ~590k at n=12. A 256x256 image produces many small layers,
        /// and Held-Karp only has to beat a 0.25-0.5s OrTools solve to be worth it.
        /// </para>
        /// </summary>
        private const int ExactMaxPoints = 12;

        // Early-exit (improvement limit) tuning, ported from upstream. Off by default: upstream's
        // author reports the values are unintuitive and did not ship them enabled either.
        private bool _earlyExitEnabled;
        private double _earlyExitRateCoefficient = 0.05;
        private int _earlyExitSolutionsDistance = 10;

        public static float GetRecommendedTSPSolveTime(int width, int height)
        {
            const int squared64 = 64 * 64;
            const int squared128 = 128 * 128;
            const int squared192 = 192 * 192;
            const int squared256 = 256 * 256;

            int pixels = width * height;
            if (pixels <= squared64)
                return 1.0f;
            else if (pixels <= squared128)
                return 3.0f;
            else if (pixels <= squared192)
                return 4.0f;
            else if (pixels <= squared256)
                return 5.0f;
            else
            {
                return 5.0f; // should never reach here...
            }
        }

        /// <summary>Common distance function: one DPad tap covers one step on both axes, so the
        /// cost of a move is the Chebyshev distance.</summary>
        internal static int Chebyshev(CanvasPoint a, CanvasPoint b) =>
            Math.Max(Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

        /// <summary>Index of the point nearest the current cursor, used as the route start.
        /// Reads live cursor state, so this is emission-time only — never call it from the
        /// parallel pre-solve.</summary>
        private int NearestIndexToCursor(CanvasPoint[] points)
        {
            int best = 0;
            var bestDist = MeasureDistanceToFromCurrent(points[0].X, points[0].Y);
            for (int i = 1; i < points.Length; i++)
            {
                var d = MeasureDistanceToFromCurrent(points[i].X, points[i].Y);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>
        /// Held-Karp exact solver for tiny point sets — an <b>open path</b> starting at the
        /// cursor-nearest point, with no return leg.
        /// <para>
        /// Ported from upstream, whose author notes this one function was AI-written and that the
        /// algorithm is standard. Kept in the serial emission path only: it starts from the live
        /// cursor, which the pre-solve cannot know. Because the result is an open path rather than a
        /// cycle, it must NOT be fed through <see cref="CutCycleNearCursor"/> — that would add a
        /// closing arc this deliberately refuses to pay for.
        /// </para>
        /// </summary>
        private List<CanvasPoint> HeldKarpRoute(CanvasPoint[] points)
        {
            int n = points.Length;
            int start = NearestIndexToCursor(points);

            // Relabel so the start is node 0, keeps the DP masks simple.
            var nodes = new CanvasPoint[n];
            nodes[0] = points[start];
            for (int i = 0, w = 1; i < n; i++)
                if (i != start)
                    nodes[w++] = points[i];

            int full = 1 << n;
            const int INF = int.MaxValue / 2;
            var dp = new int[full, n]; // dp[mask, j] = cheapest path from start visiting mask, ending at j
            var parent = new int[full, n];

            for (int mask = 0; mask < full; mask++)
            for (int j = 0; j < n; j++)
            {
                dp[mask, j] = INF;
                parent[mask, j] = -1;
            }

            dp[1, 0] = 0; // just the start

            for (int mask = 1; mask < full; mask++)
            {
                if ((mask & 1) == 0)
                    continue; // every path includes the start
                for (int j = 0; j < n; j++)
                {
                    if (dp[mask, j] == INF || (mask & (1 << j)) == 0)
                        continue;
                    for (int k = 0; k < n; k++)
                    {
                        if ((mask & (1 << k)) != 0)
                            continue;
                        int next = mask | (1 << k);
                        int cost = dp[mask, j] + Chebyshev(nodes[j], nodes[k]);
                        if (cost < dp[next, k])
                        {
                            dp[next, k] = cost;
                            parent[next, k] = j;
                        }
                    }
                }
            }

            // Cheapest endpoint over the full set (open path, no return to start).
            int last = 0,
                bestCost = INF;
            for (int j = 0; j < n; j++)
                if (dp[full - 1, j] < bestCost)
                {
                    bestCost = dp[full - 1, j];
                    last = j;
                }

            // Walk parents back to rebuild the order.
            var order = new CanvasPoint[n];
            int m = full - 1;
            for (int idx = n - 1; idx >= 0; idx--)
            {
                order[idx] = nodes[last];
                int prev = parent[m, last];
                m ^= 1 << last;
                last = prev;
            }

            return order.ToList();
        }

        /// <summary>
        /// Greedy nearest-neighbour routing from the cursor-nearest point. Fallback for when OrTools
        /// returns no solution at all (very large layers). Reads live cursor state, so emission-time
        /// only.
        /// </summary>
        private List<CanvasPoint> NearestNeighbourRoute(List<CanvasPoint> inputPoints)
        {
#if DEBUG
            var sw = Stopwatch.StartNew();
#endif
            var points = inputPoints.ToArray();

            var ordered = new List<CanvasPoint>(points.Length);

            if (points.Length == 0)
                return ordered;

            if (points.Length == 1)
            {
                ordered.Add(points[0]);
                return ordered;
            }

            // We are just going to go to the nearest point repeatedly.
            var currentIndex = NearestIndexToCursor(points);
            ordered.Add(points[currentIndex]);
            var visited = new bool[points.Length];
            visited[currentIndex] = true;

            for (int i = 0; i < points.Length - 1; i++)
            {
                var cur = points[currentIndex];
                int nearestIndex = -1;
                int nearestDist = int.MaxValue;

                for (int j = 0; j < points.Length; j++)
                {
                    if (visited[j])
                        continue;
                    int dist = Chebyshev(points[j], cur);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearestIndex = j;
                    }
                }

                visited[nearestIndex] = true;
                ordered.Add(points[nearestIndex]);
                currentIndex = nearestIndex;
            }
#if DEBUG
            sw.Stop();
            _log($"\tNearest-neighbour TSP took {sw.ElapsedMilliseconds}ms");
#endif

            return ordered;
        }

        /// <summary>
        /// Routes a set of points into a draw order: exact for tiny layers, OrTools for the rest.
        /// Returns <c>null</c> only when OrTools finds no solution at all, which is what lets the
        /// fine-detail phase fall back to snaking — so the nullable contract is deliberate and
        /// differs from upstream's (theirs internalises the fallback and can never report failure).
        /// </summary>
        private List<CanvasPoint>? PerformTSP(
            List<CanvasPoint> inputPoints,
            float timeLimitSeconds,
            out double solveMs
        )
        {
            solveMs = 0;

            // Defensive: callers currently guard against empty input, but never crash here.
            if (inputPoints.Count == 0)
                return new List<CanvasPoint>();

            var points = inputPoints.ToArray();

            if (points.Length == 1)
                return new List<CanvasPoint>(inputPoints);

            // Small enough to solve exactly - faster and better than the heuristic, and dodges the
            // improvement limit being flaky on tiny instances.
            if (points.Length <= ExactMaxPoints)
                return HeldKarpRoute(points);

            int closestPointIndex = NearestIndexToCursor(points);

            using var manager = new RoutingIndexManager(points.Length, 1, closestPointIndex);
            using var routing = new RoutingModel(manager);

            int transitCallbackIndex = routing.RegisterTransitCallback(
                (fromIndex, toIndex) =>
                {
                    var fromNode = manager.IndexToNode(fromIndex);
                    var toNode = manager.IndexToNode(toIndex);
                    // A note: during testing I made a change trying to incentivize adjacent things
                    // since it can just hold A during... but the lowest value this can return is 1
                    // so there was no gain, it was already trying to do that lol.
                    return Chebyshev(points[fromNode], points[toNode]);
                }
            );

            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

            // Single source of truth for the solver configuration, shared with RouteSolver so the
            // parallel path cannot silently drift from this one.
            var searchParameters = RouteSolver.BuildSearchParameters(
                timeLimitSeconds,
                _earlyExitEnabled,
                _earlyExitRateCoefficient,
                _earlyExitSolutionsDistance
            );

            var sw = Stopwatch.StartNew();
            var solution = routing.SolveWithParameters(searchParameters);
            sw.Stop();
            solveMs = sw.Elapsed.TotalMilliseconds;

            if (solution is null)
                return null;

            var optimizedRoute = new List<CanvasPoint>(points.Length);
            long index = routing.Start(0);
            while (routing.IsEnd(index) == false)
            {
                optimizedRoute.Add(points[manager.IndexToNode(index)]);
                index = solution.Value(routing.NextVar(index));
            }

            return optimizedRoute;
        }
    }
}
