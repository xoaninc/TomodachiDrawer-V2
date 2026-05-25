// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>
// Modified version of TomodachiDrawer (original (C) Lucas7yoshi, GPL-3.0).

using System.Diagnostics;
using Google.OrTools.ConstraintSolver;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.Core
{
    /// <summary>
    /// Pure, thread-safe TSP solve over a set of canvas points. Same OR-Tools configuration as
    /// <see cref="CanvasDrawer.PerformTSP"/> (Chebyshev arc cost, PathCheapestArc + GuidedLocalSearch,
    /// time limit) but with the depot fixed at index 0 and NO dependency on cursor/instance state —
    /// every call builds its own <see cref="RoutingModel"/>, so N calls run concurrently without
    /// interference.
    /// <para>
    /// Used by (a) the Routing Lab's parallel-speedup benchmark and (b) the production parallel
    /// pre-solve stage in <see cref="CanvasDrawer.DrawImage"/>. The OR-Tools routing solver is
    /// itself single-threaded, so a parallel batch is N independent single-threaded solves.
    /// </para>
    /// Returns a permutation of <paramref name="inputPoints"/> (every input point appears exactly
    /// once), or an empty list if OR-Tools returns no solution (callers fall back to input order).
    /// </summary>
    public static class RouteSolver
    {
        public static List<CanvasPoint> Solve(
            IReadOnlyList<CanvasPoint> inputPoints,
            float timeLimitSeconds,
            out double solveMs
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

            var manager = new RoutingIndexManager(points.Length, 1, 0);
            var routing = new RoutingModel(manager);

            int transitCallbackIndex = routing.RegisterTransitCallback(
                (fromIndex, toIndex) =>
                {
                    var fromNode = manager.IndexToNode(fromIndex);
                    var toNode = manager.IndexToNode(toIndex);
                    return Math.Max(
                        Math.Abs(points[fromNode].X - points[toNode].X),
                        Math.Abs(points[fromNode].Y - points[toNode].Y)
                    );
                }
            );

            routing.SetArcCostEvaluatorOfAllVehicles(transitCallbackIndex);

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
            int seconds = (int)timeLimitSeconds;
            int nanoseconds = (int)((timeLimitSeconds - seconds) * 1_000_000_000);
            searchParameters.TimeLimit = new Google.Protobuf.WellKnownTypes.Duration
            {
                Seconds = seconds,
                Nanos = nanoseconds,
            };

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
    }
}
