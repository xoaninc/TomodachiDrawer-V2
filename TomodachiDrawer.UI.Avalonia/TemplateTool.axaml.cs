using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

using SkiaSharp;

using TomodachiDrawer.Core.Extensions;
using TomodachiDrawer.Core.ImageProcessing;

namespace TomodachiDrawer.UI.Avalonia;

public partial class TemplateTool : Window
{
    private readonly TomodachiLifeMask _mask;


    private SKBitmap _currentPreview = new SKBitmap(256, 256); // this is just to shut up a warning :<

    // For preview
    public TemplateTool() : this(TomodachiLifeMask.BodyTopsLongH) { }

    public TemplateTool(TomodachiLifeMask mask)
    {
        _mask = mask;
        InitializeComponent();

        this.Title = "Template Tool - " + _mask.GetDescription();

        // Load the mask
        var skiaMask = ImageMasker.GetMask(_mask);
        if (skiaMask == null)
        {
            // Couldnt load the mask...
            this.Close(new TemplateToolResponse(false, true, null));
        }
        else
        {
            var avaMask = MainWindow.ToAvaloniaBitmap(skiaMask);
            _currentPreview = skiaMask;

            var checker = GenerateCheckerboard(skiaMask.Width, skiaMask.Height, cellSize: 8);
            CheckerboardPreview.Source = MainWindow.ToAvaloniaBitmap(checker);
            TemplatePreview.Source = MainWindow.ToAvaloniaBitmap(MakeBetterMask(skiaMask));

            Opened += TemplateTool_Opened;
        }

    }

    private async void SetClipboardBitmap(Bitmap bitmap)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is { } clipboard)
        {
            await clipboard.SetBitmapAsync(bitmap);
        }
    }

    private async Task ShowMessageAsync(
        string title,
        string message
    )
    {
        var buttonRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };

        var okButton = new Button
        {
            Content = "OK",
            Margin = new Thickness(0, 10, 0, 0),
            MinWidth = 80,
        };

        var stack = new StackPanel() { Margin = new Thickness(16) };
        buttonRow.Children.Add(okButton);


        stack.Children.Insert(
            0,
            new SelectableTextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 400,
            }
        );
        stack.Children.Add(buttonRow);

        var dialog = new Window
        {
            Title = title,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            Content = stack,
        };

        okButton.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(this);
    }

    private async void TemplateTool_Opened(object? sender, EventArgs e)
    {
        if (TemplatePreview.Source is Bitmap bitmap)
            SetClipboardBitmap(bitmap);
    }

    private async void CopyClipboardButton_Click(object? sender, RoutedEventArgs e)
    {
        if (TemplatePreview.Source is Bitmap bitmap)
            SetClipboardBitmap(bitmap);
    }

    private static SKBitmap GenerateCheckerboard(int width, int height, int cellSize = 8)
    {
        var bmp = new SKBitmap(width, height);
        var colA = new SKColor(0x2A, 0x2A, 0x2A, 0xFF);
        var colB = new SKColor(0x38, 0x38, 0x38, 0xFF);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool even = ((x / cellSize) + (y / cellSize)) % 2 == 0;
                bmp.SetPixel(x, y, even ? colA : colB);
            }
        }
        return bmp;
    }

    private static SKBitmap MakeBetterMask(SKBitmap mask)
    {
        using var nodrawStream = AssetLoader.Open(new Uri("avares://TomodachiDrawer.UI.Avalonia/Assets/nodraw.png"));
        var nodraw = SKBitmap.Decode(nodrawStream);
        var tinted = mask.Copy();
        for (int y = 0; y < tinted.Height; y++)
        {
            for (int x = 0; x < tinted.Width; x++)
            {
                var px = tinted.GetPixel(x, y);
                if (px.Alpha > 0)
                    tinted.SetPixel(x, y, nodraw.GetPixel(x, y));
            }
        }
        return tinted;
    }

    // This could probably be more efficent.
    public static SKBitmap ToSKBitmap(Bitmap avaloniaBitmap)
    {
        ArgumentNullException.ThrowIfNull(avaloniaBitmap);
        using var stream = new MemoryStream();
        avaloniaBitmap.Save(stream);
        stream.Seek(0, SeekOrigin.Begin);
        return SKBitmap.Decode(stream);
    }

    private async void PasteButton_Click(object? sender, RoutedEventArgs e)
    {
        // Read the image from the clipboard, convert it to SKBitmap, apply the mask, and update preview.
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.Clipboard is { } clipboard)
        {
            var bitmap = await clipboard.TryGetBitmapAsync();
            if (bitmap == null) // fallback to try and read a file off disk...
            {
                // try and fetch a file path instead...
                var path = await clipboard.TryGetFilesAsync();
                if (path?.Length > 0 && File.Exists(path[0].Path.LocalPath))
                {
                    bitmap = new Bitmap(path.First().Path.LocalPath);
                }
            }

            if (bitmap != null)
            {
                // Convert to SKBitmap and mask it off
                var skiaBitmap = ToSKBitmap(bitmap);
                var masked = ImageMasker.MaskImage(skiaBitmap, ImageMasker.GetMask(_mask));
                // update preview image — composite the masked result with the red mask overlay
                _currentPreview.Dispose();
                _currentPreview = masked;
                TemplatePreview.Source = MainWindow.ToAvaloniaBitmap(masked);
            }
            else
            {
                await ShowMessageAsync(
                    $"Error",
                    "Could not find an image on your clipboard. Make sure you copied it."
                );
            }
        }
    }

    private void ConfirmButton_Click(object? sender, RoutedEventArgs e)
    {
        var trimmedImage = _currentPreview;

        this.Close(new TemplateToolResponse(true, false, trimmedImage)); // Replace DEFAULT with the right thing
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        this.Close(new TemplateToolResponse(false, false, null));
    }
}