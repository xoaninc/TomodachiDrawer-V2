namespace TomodachiDrawer.Core.Models
{
    /// <summary>
    /// A route produced by the parallel pre-solve, tagged with whether it is a closed cycle.
    /// <para>
    /// The distinction is load-bearing. An OR-Tools tour is a Hamiltonian <b>cycle</b> — its cost
    /// includes the arc back to the depot — so it can be cut anywhere and re-traversed from the cut
    /// without changing which arcs get paid for. The pre-solve's nearest-neighbour fallback is an
    /// <b>open path</b> with no closing arc, so cutting it anywhere other than its ends would make
    /// the emission walk a phantom arc the solver never accounted for.
    /// </para>
    /// </summary>
    /// <param name="Points">The visiting order. Always a permutation of the input points.</param>
    /// <param name="IsCycle">
    /// True for an OR-Tools tour, false for the nearest-neighbour fallback.
    /// </param>
    public readonly record struct PreSolvedRoute(List<CanvasPoint> Points, bool IsCycle);
}
