// SPDX-License-Identifier: GPL-3.0-or-later
// TomodachiDrawer V2 — Copyright (C) 2026 Xoan <github.com/xoaninc>

using System.Security.Cryptography;
using System.Text;
using SkiaSharp;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.Core
{
    /// <summary>
    /// Hash of everything that affects a generated route. Two calls producing the same fingerprint
    /// may reuse a cached <c>.tdld</c>.
    /// <para>
    /// This lives in Core, next to <see cref="DrawImageSettings"/>, on purpose: knowing which
    /// settings change the route is Core's business, and putting it here makes it testable. Miss a
    /// field and toggling that setting silently returns the PREVIOUS drawing from the cache while
    /// the log claims the route is current — so the wrong <c>.tdld</c> reaches the hardware.
    /// <c>FingerprintTests</c> walks every public property on <see cref="DrawImageSettings"/> by
    /// reflection and fails if any of them does not move the hash.
    /// </para>
    /// <para>
    /// Built by hand rather than with <c>JsonSerializer.Serialize(settings)</c>: the
    /// reflection-based overload is trim-unsafe (IL2026 under PublishTrimmed).
    /// </para>
    /// </summary>
    public static class RouteFingerprint
    {
        public static string Compute(
            SKBitmap image,
            DrawImageSettings settings,
            SwitchVersion version
        )
        {
            using var ms = new MemoryStream();
            var pixels = image.Bytes;
            ms.Write(pixels, 0, pixels.Length);

            var meta = Encoding.UTF8.GetBytes(
                Describe(image.Width, image.Height, settings, version)
            );
            ms.Write(meta, 0, meta.Length);
            ms.Position = 0;
            return Convert.ToHexString(SHA256.HashData(ms));
        }

        /// <summary>
        /// The settings half of the fingerprint, split out so tests can compare it without building
        /// bitmaps. Every route-affecting property of <see cref="DrawImageSettings"/> must appear.
        /// </summary>
        internal static string Describe(
            int width,
            int height,
            DrawImageSettings settings,
            SwitchVersion version
        )
        {
            var q = settings.QuantizerSettings;
            return $"|{width}x{height}|{version}"
                + $"|q={q.quantizerName}|c={q.colourCount}|dith={q.useDithering}"
                + $"|denoise={settings.DenoiserName}"
                + $"|tsp={settings.TSPTimeLimit}"
                + $"|nolb={settings.DisableLargeBrush}"
                + $"|exp={settings.EnableExperimentalFeatures}"
                + $"|home={settings.HomeToTopLeft}"
                + $"|rev={settings.ReverseColourOrder}"
                + $"|ee={settings.EarlyTspExitEnabled}"
                + $"|eerc={settings.EarlyTspExitRateCoefficient}"
                + $"|eesd={settings.EarlyTspExitSolutionsDistance}";
        }
    }
}
