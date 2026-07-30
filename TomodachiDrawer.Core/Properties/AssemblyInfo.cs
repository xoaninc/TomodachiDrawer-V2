using System.Runtime.CompilerServices;

// ChooseCut/ApplyCut are internal so the cut decision can be brute-force checked in tests
// without standing up a live CanvasDrawer.
[assembly: InternalsVisibleTo("TomodachiDrawer.Core.Tests")]
