using TomodachiDrawer.Core;
using TomodachiDrawer.Core.OutputSinks;
#if !UPSTREAM
using TomodachiDrawer.Core.Interfaces;
#endif


namespace TomodachiDrawer.Bench
{
    /// <summary>
    /// Counts what a drawing costs, by being replayed the action stream a
    /// <see cref="TimingSink"/> recorded — the same way the app replays into a
    /// <c>FileControllerSink</c> to get bytes.
    /// </summary>
    public sealed class MetricsSink : ISwitchOutput
    {
        // Historical heuristic for counting paint actions. Explicit-duration bucket clicks are
        // excluded, so this is descriptive only; rendered pixels establish actual coverage.
        private const double DefaultHold = 25.0;
        private const double DefaultRelease = 25.0;

        private bool _aHeld;

        /// <summary>
        /// <b>The route-cost metric.</b> <c>NavigateTo</c> emits exactly one DPad tap per cursor
        /// step and diagonals move both axes in one tap, so this count *is* the Chebyshev
        /// navigation cost within the canvas. This total also includes menu navigation, which
        /// can change when solver arms choose different bucket/brush strategies. Report it as
        /// total DPad actions rather than interpreting every delta as pen travel.
        /// </summary>
        public long DPadTaps { get; private set; }

        /// <summary>
        /// <b>A historical paint-action estimate, not a coverage invariant.</b> Counts default
        /// A taps plus fine-detail cells crossed while holding A. Explicit-duration bucket taps
        /// are not recognized, and a stamp or fill can paint many cells with one action.
        /// Different winning bucket/brush strategies legitimately change this value. Use the
        /// pixel renderer and decoded-input checks to decide correctness.
        /// </summary>
        public long PaintActions { get; private set; }

        /// <summary>
        /// <b>The hold-run metric</b>, and the discriminator for whether pricing a run break in the
        /// solver objective actually did anything. One <c>Press(A)</c> starts exactly one A-hold run,
        /// so this is the <c>groups</c> term in <c>travel + groups</c>: the number of times the route
        /// left a run of adjacent cells and had to release and re-press.
        /// <para>
        /// Unlike route cost this has <b>no wall-clock component</b>, so it is exact. Total taps carry
        /// ~0,7% run-to-run noise on a 256² image because the OrTools time limit is a wall-clock
        /// budget, which is more than the whole expected effect — so a tap delta cannot decide the
        /// question and this can.
        /// </para>
        /// <para>
        /// Lower may indicate a better route; both this count and <see cref="PaintActions"/>
        /// can legitimately change across strategies. The expected rendered image must not.
        /// </para>
        /// </summary>
        public long HoldRuns { get; private set; }

        public long StickMoves { get; private set; }
        public double TotalMilliseconds { get; private set; }

        public void Delay(double milliseconds) => TotalMilliseconds += milliseconds;

        public void Press(Button btn)
        {
            if (btn == Button.A)
            {
                // Start of an A-hold run: paints the cell under the cursor. Menu confirmations go
                // through Tap(Button.A, ...) instead, so Press(A) counts runs and nothing else.
                _aHeld = true;
                PaintActions++;
                HoldRuns++;
            }
        }

        public void Release(Button btn)
        {
            if (btn == Button.A)
                _aHeld = false;
        }

        public void Press(DPad dir) => Step();

        public void Release(DPad dir) { }

        public void ReleaseAll() => _aHeld = false;

        public void SetStick(Stick stick, byte value) => StickMoves++;

        private void Step()
        {
            DPadTaps++;
            if (_aHeld)
                PaintActions++; // moved one cell with A down, so that cell is painted
        }

        // TimingSink records Tap as a single action at the default durations, so these explicit
        // implementations are what the replay lands on. Time accounting mirrors TimingSink exactly
        // so TotalMilliseconds can be cross-checked against it.
        void ISwitchOutput.Tap(Button btn, double holdDuration, double releaseDuration)
        {
            if (btn == Button.A && holdDuration == DefaultHold && releaseDuration == DefaultRelease)
                PaintActions++; // isolated fine-detail point, a stamp, or a bucket click
            TotalMilliseconds += holdDuration + releaseDuration;
        }

        void ISwitchOutput.Tap(DPad dir, double holdDuration, double releaseDuration)
        {
            Step();
            TotalMilliseconds += holdDuration + releaseDuration;
        }

        void ISwitchOutput.TapStick(
            Stick stick,
            byte value,
            double holdDuration,
            double releaseDuration
        )
        {
            StickMoves++;
            TotalMilliseconds += holdDuration + releaseDuration;
        }

        public void Dispose() { }
    }
}
