namespace TomodachiDrawer.Core.ImageProcessing.Quantizers
{
    /// <param name="useDithering">
    /// Currently unused. The "Arbitrary" quantizer lost its Floyd-Steinberg path when it moved from
    /// ImageSharp to JeremyAnsel.ColorQuant, and the UI never set this in the first place (it always
    /// passed the default). Kept, matching upstream, because dithering is a plausible thing to add
    /// back and it costs nothing to reserve — but nothing reads it except the route fingerprint.
    /// </param>
    public record QuantizerSettings(
        string quantizerName,
        int? colourCount = null,
        bool? useDithering = null
    );
}
