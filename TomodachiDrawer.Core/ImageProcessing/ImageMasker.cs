using SkiaSharp;

using System;
using System.Collections.Generic;
using System.Text;

namespace TomodachiDrawer.Core.ImageProcessing
{
    public static class ImageMasker
    {

        public static void GetMask(string name)
        {
            var assembly = typeof(CanvasDrawer).Assembly.GetManifestResourceNames();
            Console.WriteLine(assembly[1]);
        }

        public static SKBitmap MaskImage(SKBitmap input, SKBitmap mask)
        {
            if (input.Width != mask.Width || input.Height != mask.Height)
                throw new ArgumentException("Input and mask must be the same size.");

            // The tomodachi life masks are WHITE for excluded areas, and BLACK&TRANSPARENT for included areas.
            // So we remomve anything fromm the input thats in the white areas of the mask.
            // For simplicities sake, just using .Alpha as the mask.

            var output = new SKBitmap(input.Width, input.Height);
            for (int y = 0; y < mask.Height; y++)
            {
                for (int x = 0; x < mask.Width; x++)
                {
                    var maskPixel = mask.GetPixel(x, y);
                    var inputPixel = input.GetPixel(x, y);

                    if (maskPixel.Alpha == 255) // full white and no transparency is masked OUT
                        output.SetPixel(x, y, SKColors.Transparent); // Transparent
                    else
                        output.SetPixel(x, y, inputPixel);
                }
            }

            return output;
        }
    }
}
