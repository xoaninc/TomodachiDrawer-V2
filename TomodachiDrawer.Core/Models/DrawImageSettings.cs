using TomodachiDrawer.Core.ImageProcessing.Quantizers;

namespace TomodachiDrawer.Core.Models
{
    public class DrawImageSettings
    {
        public required QuantizerSettings QuantizerSettings { get; set; }

        public string? DenoiserName { get; set; } = null;

        public float TSPTimeLimit { get; set; } = 1.0f;

        /// <summary>Disables "stamp" detection, which is areas that could be drawn with 3x3, 5x5, 9x9, etc brushes to save time.</summary>
        public bool DisableLargeBrush { get; set; } = false;

        /// <summary>Enables stuff that may be prone to desyncs or other instabilities.</summary>
        public bool EnableExperimentalFeatures { get; set; } = false;

        public bool HomeToTopLeft { get; set; } = false;

        public bool ReverseColourOrder { get; set; } = false;

        /// <summary>
        /// Lets the TSP solver stop once it stops improving, instead of always burning the whole
        /// time limit. Off by default: upstream's author shipped it disabled because the two
        /// coefficients below are unintuitive to tune, and testing suggested a sore spot around
        /// 16-40 points where it does not settle.
        /// </summary>
        public bool EarlyTspExitEnabled { get; set; } = false;

        public double EarlyTspExitRateCoefficient { get; set; } = 0.05;

        public int EarlyTspExitSolutionsDistance { get; set; } = 10;

        /// <summary>
        /// Whether taps are timed in milliseconds or in host polls. See <see cref="TapTimingMode"/>
        /// — <c>HidPolls</c> is experimental and RP2040/RP2350 only.
        /// </summary>
        public TapTimingMode TapTiming { get; set; } = TapTimingMode.Milliseconds;

        /// <summary>
        /// Host polls a tap is held for under <see cref="TapTimingMode.HidPolls"/>. Upstream's
        /// default, credited in their firmware comment to "wigreal's findings".
        /// </summary>
        public ushort TapHoldPolls { get; set; } = 22;

        /// <summary>Host polls a tap stays released for under <see cref="TapTimingMode.HidPolls"/>.</summary>
        public ushort TapReleasePolls { get; set; } = 3;
    }
}
