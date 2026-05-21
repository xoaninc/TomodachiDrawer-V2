using SkiaSharp;

namespace TomodachiDrawer.UI.Avalonia
{
    public record TemplateToolResponse(bool Success, bool couldntLoad, SKBitmap? Result);
}
