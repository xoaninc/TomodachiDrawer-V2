namespace TomodachiDrawer.Core.Models
{
    /// <summary>
    /// Which routing phase a parallel pre-solved route belongs to — the key the
    /// <c>BuildPreRoutes</c> dictionary is built on so the emission loop can look each route back up.
    /// </summary>
    public enum RoutingPhaseKind
    {
        Stamp,
        FineDetail,
        BucketClicks,
    }
}
