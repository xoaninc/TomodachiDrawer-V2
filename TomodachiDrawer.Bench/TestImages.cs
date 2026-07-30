using SkiaSharp;

namespace TomodachiDrawer.Bench
{
    /// <summary>
    /// Deterministic synthetic test images, generated with integer maths only so a baseline
    /// captured today is reproducible later. No files, no <c>Random</c>, no assets to commit.
    /// <para>
    /// Each one targets a different part of the pipeline: uniform blocks produce large-brush
    /// "stamps", thin curves produce fine detail (the phase whose TSP dominates), and the full-size
    /// opaque image triggers the bucket-fill path.
    /// </para>
    /// </summary>
    public static class TestImages
    {
        public static IReadOnlyList<(string Name, SKBitmap Bitmap)> All() =>
            [("grid64", Grid64()), ("rings128", Rings128()), ("mosaic256", Mosaic256())];

        /// <summary>64x64, chunky blocks with a transparent border — exercises stamps plus the
        /// transparent (non-bucket-fill) path.</summary>
        private static SKBitmap Grid64()
        {
            const int n = 64;
            var px = new SKColor[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                bool border = x < 6 || y < 6 || x >= n - 6 || y >= n - 6;
                if (border)
                {
                    px[y * n + x] = SKColors.Transparent;
                    continue;
                }
                int bx = x / 8;
                int by = y / 8;
                int idx = (bx * 3 + by * 5) % 8;
                px[y * n + x] = Wheel(idx, 8);
            }
            return FromPixels(n, n, px);
        }

        /// <summary>128x128 concentric rings — thin curved shapes, so most points land in the
        /// fine-detail phase where the TSP time limit actually bites.</summary>
        private static SKBitmap Rings128()
        {
            const int n = 128;
            const int c = n / 2;
            var px = new SKColor[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int dx = x - c;
                int dy = y - c;
                // Integer distance, no floating point, so this is bit-stable across machines.
                int r = ISqrt(dx * dx + dy * dy);
                px[y * n + x] = Wheel((r / 4) % 24, 24);
            }
            return FromPixels(n, n, px);
        }

        /// <summary>256x256, fully opaque, four different structures in quadrants — the heavy case,
        /// and the one that triggers bucket fill on Switch 2 for full-size opaque images.</summary>
        private static SKBitmap Mosaic256()
        {
            const int n = 256;
            var px = new SKColor[n * n];
            for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
            {
                int half = n / 2;
                int idx;
                if (x < half && y < half) // blocks
                    idx = ((x / 16) * 7 + (y / 16) * 11) % 64;
                else if (x >= half && y < half) // rings
                {
                    int dx = x - (half + half / 2);
                    int dy = y - half / 2;
                    idx = (ISqrt(dx * dx + dy * dy) / 3) % 64;
                }
                else if (x < half) // diagonal stripes
                    idx = ((x + y) / 5) % 64;
                else
                {
                    // Deterministic LCG-style hash of the coordinate — structureless detail, which
                    // is the worst case for routing.
                    uint h = (uint)(x * 1973 + y * 9277 + 26699);
                    h ^= h >> 13;
                    h *= 1274126177u;
                    h ^= h >> 16;
                    idx = (int)(h % 64);
                }
                px[y * n + x] = Wheel(idx, 64);
            }
            return FromPixels(n, n, px);
        }

        /// <summary>Spreads <paramref name="count"/> distinct opaque colours around the hue wheel
        /// with varying value, so the quantizer has real work to do.</summary>
        private static SKColor Wheel(int index, int count)
        {
            float hue = 360f * index / count;
            float value = 55f + 45f * ((index % 3) / 2f);
            return SKColor.FromHsv(hue, 70f + 30f * ((index % 2)), value);
        }

        private static int ISqrt(int v)
        {
            if (v <= 0)
                return 0;
            int r = 0;
            while ((r + 1) * (r + 1) <= v)
                r++;
            return r;
        }

        private static SKBitmap FromPixels(int w, int h, SKColor[] px)
        {
            var bmp = new SKBitmap(w, h);
            bmp.Pixels = px;
            return bmp;
        }
    }
}
