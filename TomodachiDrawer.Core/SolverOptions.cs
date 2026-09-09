namespace TomodachiDrawer.Core;

/// <summary>One immutable per-generation configuration shared by every solver invocation.</summary>
internal sealed record SolverOptions(
    bool EarlyExitEnabled,
    double EarlyExitRateCoefficient,
    int EarlyExitSolutionsDistance
);
