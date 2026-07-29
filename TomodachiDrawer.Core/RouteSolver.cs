// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

using System.Diagnostics;
using Google.OrTools.ConstraintSolver;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.Core
{
    /// <summary>
    /// Pure, thread-safe TSP solve over a set of canvas points. Uses the same OR-Tools
    /// configuration as <see cref="CanvasDrawer.PerformTSP"/> — literally the same, via
    /// <see cref="BuildSearchParameters"/> — but with the depot fixed at index 0 and NO dependency
    /// on cursor/instance state, so N calls run concurrently without interference.
    /// <para>
    /// Used by the parallel pre-solve stage in <see cref="CanvasDrawer.DrawImage"/>. The OR-Tools
    /// routing solver is itself single-threaded, so a parallel batch is N independent
    /// single-threaded solves.
    /// </para>
    /// <para>
    /// The depot is pinned to 0 because the pre-solve runs before emission and cannot know each
    /// layer's starting cursor. That costs nothing in tour quality (the objective is a closed cycle,
    /// so the optimum is depot-invariant) — the emission side then picks the best place to cut the
    /// cycle via <c>CutCycleNearCursor</c>.
    /// </para>
    /// Returns a permutation of <paramref name="inputPoints"/> (every input point appears exactly
    /// once), or an empty list if OR-Tools returns no solution.
    /// </summary>
    public static class RouteSolver
    {
        /// <summary>
        /// The one definition of the solver configuration, shared with
        /// <see cref="CanvasDrawer.PerformTSP"/>. Both paths must stay identical or the parallel
        /// route silently stops matching the serial one, so there is deliberately no second copy.
        /// </summary>
        public static RoutingSearchParameters BuildSearchParameters(
            float timeLimitSeconds,
            bool earlyExitEnabled = false,
            double earlyExitRateCoefficient = 0.05,
            int earlyExitSolutionsDistance = 10
        )
        {
            var searchParameters =
                operations_research_constraint_solver.DefaultRoutingSearchParameters();
            searchParameters.FirstSolutionStrategy = FirstSolutionStrategy
                .Types
                .Value
                .PathCheapestArc;
            searchParameters.LocalSearchMetaheuristic = LocalSearchMetaheuristic
                .Types
                .Value
                .GuidedLocalSearch;

            // need to get int seconds and int nanoseconds because... google.
            int seconds = (int)timeLimitSeconds;
            int nanoseconds = (int)((timeLimitSeconds - seconds) * 1_000_000_000);
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration
            {
                Seconds = seconds,
                Nanos = nanoseconds,
            };

            // Both of these have to be set for the early exit to engage.
            if (earlyExitEnabled)
                searchParameters.ImprovementLimitParameters =
                    new RoutingSearchParameters.Types.ImprovementSearchLimitParameters
                    {
                        ImprovementRateCoefficient = earlyExitRateCoefficient,
                        ImprovementRateSolutionsDistance = earlyExitSolutionsDistance,
                    };

            return searchParameters;
        }

        public static List<CanvasPoint> Solve(
            IReadOnlyList<CanvasPoint> inputPoints,
            float timeLimitSeconds,
            out double solveMs,
            bool earlyExitEnabled = false,
            double earlyExitRateCoefficient = 0.05,
            int earlyExitSolutionsDistance = 10
        )
        {
            if (inputPoints.Count == 0)
            {
                solveMs = 0;
                return new List<CanvasPoint>();
            }

            if (inputPoints.Count == 1)
            {
                // OR-Tools' 1-node tour extracts to an empty route; return the point directly so the
                // contract (non-empty input → permutation) holds and no single pixel is ever dropped.
                solveMs = 0;
                return new List<CanvasPoint>(inputPoints);
            }

            var points = new CanvasPoint[inputPoints.Count];
            for (int i = 0; i < inputPoints.Count; i++)
                points[i] = inputPoints[i];

            using var manager = new RoutingIndexManager(points.Length, 1, 0);
            using var routing = new RoutingModel(manager);

            int transitCallbackIndex = routing.RegisterTransitCallback(
                (fromIndex, toIndex) =>
                {
                    var fromNode = manager.IndexToNode(fromIndex);
                    var toNode = manager.IndexToNode(toIndex);
                    return CanvasDrawer.Chebyshev(points[fromNode], points[toNode]);
                }
            );

            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

            var searchParameters = BuildSearchParameters(
                timeLimitSeconds,
                earlyExitEnabled,
                earlyExitRateCoefficient,
                earlyExitSolutionsDistance
            );

            var sw = Stopwatch.StartNew();
            var solution = routing.SolveWithParameters(searchParameters);
            sw.Stop();
            solveMs = sw.Elapsed.TotalMilliseconds;

            var optimizedRoute = new List<CanvasPoint>(points.Length);
            if (solution is null)
                return optimizedRoute;

            long index = routing.Start(0);
            while (routing.IsEnd(index) == false)
            {
                optimizedRoute.Add(points[manager.IndexToNode(index)]);
                index = solution.Value(routing.NextVar(index));
            }

            return optimizedRoute;
        }

        /// <summary>
        /// Cursor-free greedy nearest-neighbour from an explicit start index. Used as the pre-solve's
        /// fallback when OR-Tools returns nothing: the emission path has
        /// <c>CanvasDrawer.NearestNeighbourRoute</c> for the same job, but that one starts from the
        /// live cursor, which during pre-solve is still sitting on an earlier layer.
        /// </summary>
        public static List<CanvasPoint> NearestNeighbourFrom(
            IReadOnlyList<CanvasPoint> inputPoints,
            int startIndex
        )
        {
            int n = inputPoints.Count;
            var ordered = new List<CanvasPoint>(n);
            if (n == 0)
                return ordered;
            if (n == 1)
            {
                ordered.Add(inputPoints[0]);
                return ordered;
            }

            int current = Math.Clamp(startIndex, 0, n - 1);
            var visited = new bool[n];
            visited[current] = true;
            ordered.Add(inputPoints[current]);

            for (int i = 0; i < n - 1; i++)
            {
                var cur = inputPoints[current];
                int nearest = -1;
                int nearestDist = int.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (visited[j])
                        continue;
                    int dist = CanvasDrawer.Chebyshev(inputPoints[j], cur);
                    if (dist < nearestDist)
                    {
                        nearestDist = dist;
                        nearest = j;
                    }
                }
                visited[nearest] = true;
                ordered.Add(inputPoints[nearest]);
                current = nearest;
            }

            return ordered;
        }
    }
}
