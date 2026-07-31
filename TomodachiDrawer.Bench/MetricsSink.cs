using TomodachiDrawer.Core.Interfaces;
using TomodachiDrawer.Core.OutputSinks;

namespace TomodachiDrawer.Bench
{
    /// <summary>
    /// Counts what a drawing costs, by being replayed the action stream a
    /// <see cref="TimingSink"/> recorded — the same way the app replays into a
    /// <c>FileControllerSink</c> to get bytes.
    /// </summary>
    public sealed class MetricsSink : ISwitchOutput
    {
        // A tap at exactly the ISwitchOutput default durations is a drawing action; every menu
        // confirmation in CanvasToolbar / ColourPalette / ConnectAndConfirmController passes an
        // explicit non-default duration. Fragile but it is the only signal in the stream.
        private const double DefaultHold = 25.0;
        private const double DefaultRelease = 25.0;

        private bool _aHeld;

        /// <summary>
        /// <b>The route-cost metric.</b> <c>NavigateTo</c> emits exactly one DPad tap per cursor
        /// step and diagonals move both axes in one tap, so this count *is* the Chebyshev
        /// navigation cost. Caveat: menu navigation also taps the DPad, so the absolute number
        /// mixes canvas travel with menu travel. That does not affect comparisons — for a fixed
        /// image and settings the menu travel is identical across solver arms, so any *delta* is
        /// pure route cost.
        /// </summary>
        public long DPadTaps { get; private set; }

        /// <summary>
        /// <b>The coverage check.</b> Cells actually painted: one per stamp, one per bucket click,
        /// one per isolated fine-detail point, and one per cell of an A-hold run (the
        /// <c>Press(A)</c> paints the cell under the cursor, then each held DPad step paints the
        /// next — see <c>CanvasDrawer.FineDetailTsp</c>).
        /// <para>
        /// This is invariant to route order and <b>must be identical across solver arms</b> for a
        /// given image: same layers, same points, only the visiting order differs. If it moves,
        /// points were dropped or duplicated — which route cost and draw time would both
        /// misreport as an improvement.
        /// </para>
        /// <para>
        /// Note the A-press *event* count is NOT a coverage metric: a run of k adjacent cells is
        /// one press, so fewer presses can mean a better route rather than fewer pixels.
        /// </para>
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
        /// Lower is better, and unlike <see cref="PaintActions"/> it is <i>not</i> invariant: it is
        /// supposed to move. Coverage is what must not move.
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
