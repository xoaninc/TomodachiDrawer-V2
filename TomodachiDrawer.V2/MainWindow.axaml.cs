using System.IO.Ports;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Microsoft.Win32;
using SkiaSharp;
using TomodachiDrawer.Core;
using TomodachiDrawer.Core.Extensions;
using TomodachiDrawer.Core.ImageProcessing;
using TomodachiDrawer.Core.ImageProcessing.Denoising;
using TomodachiDrawer.Core.ImageProcessing.Quantizers;
using TomodachiDrawer.Core.Models;
using TomodachiDrawer.Core.OutputSinks;
using Button = Avalonia.Controls.Button; // conflict with the Button enum in SinkEnums
#if DEBUG
using TomodachiDrawer.DebugTools;
#endif

namespace TomodachiDrawer.UI.Avalonia;

public partial class MainWindow : Window
{
    private string _currentImagePath = string.Empty;
    private SKBitmap? _currentImage;
    private readonly CancellationTokenSource _cts = new();

    private bool BusyExporting = false;

    /// <summary>
    /// Cancels the in-progress route generation. Separate from <c>_cts</c> on purpose: that one is
    /// window-lifetime, is cancelled on close, and feeds the serial-port polling loops and the
    /// ESP32 flasher — cancelling a draw through it would stop port detection for the session.
    /// </summary>
    private CancellationTokenSource? _generationCts;

    // Cached generated route, reused across Estimate/Export when nothing changed.
    // Keyed by a fingerprint of (image pixels + draw settings + Switch version).
    private byte[]? _cachedTdld;
    private TimeSpan _cachedTime;
    private string? _cachedFingerprint;
    private readonly object _cacheLock = new(); // guards the three _cached* fields above

    //private SwitchVersion _selectedSwitchVersion = SwitchVersion.None;
    //private int _selectedThemeIndex = 0; // 0 is System.
    private AppSettings _currentSettings = new(); // All cases will result in it being non-null but IntelliSense cant see that far.
#if DEBUG
    private readonly VirtualGamepad _debugVirtualGamepad = new();

    private MenuItem? MenuDebugConnectVirtualGamepad;
    private MenuItem? MenuDebugRunInVirtualGamepad;
    private MenuItem? MenuDebugOpenVirtualGamepadController;
#endif

    public MainWindow()
    {
        InitializeComponent();

        var quantizers = ColourPalette.Quantizers.Keys.ToList();
        quantizers.Insert(0, "Arbitrary");
        ColourMatcherComboBox.ItemsSource = quantizers;
        ColourMatcherComboBox.SelectedIndex = 0;

        var denoiserSelection = new List<string> { "None" };
        denoiserSelection.AddRange(ImageDenoiser.Denoisers.Keys);

        DenoisingComboBox.ItemsSource = denoiserSelection;
        DenoisingComboBox.SelectedIndex = 0;
        DenoisingComboBox.SelectionChanged += (_, _) =>
        {
            UpdatePreview();
            DrawingOptionChanged();
        };

        InitializeTemplates();

        GetSettings();

        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);

#if DEBUG
        this.Title = $"TomodachiDrawer V2 (dev) - {GetVersionString(true)}";
#else
        this.Title = $"TomodachiDrawer V2 - {GetVersionString(false)}";
#endif

        StartPicoPolling();
        StartEsp32Polling();
        if (CheckForUpdatesCheckBox.IsChecked)
            _ = PerformAsyncUpdateCheck();

        Opened += MainWindow_Opened;
    }

    private bool IsVCRuntimeInstalled()
    {
        if (!OperatingSystem.IsWindows())
        {
            return true;
        }

        string keyPath = @"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\X64";

        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        if (key != null)
        {
            var version = key.GetValue("Version")?.ToString();
            return !string.IsNullOrEmpty(version);
        }

        return false;
    }

    private void InitializeTemplates()
    {
        foreach (var mask in Enum.GetValues<TomodachiLifeMask>().Cast<TomodachiLifeMask>())
        {
            var desc = mask.GetDescription();
            var menuItem = new MenuItem() { Header = desc };
            menuItem.Click += (s, e) => OpenTemplate(mask);
            MenuTemplates.Items.Add(menuItem);
        }
    }

    private async void OpenTemplate(TomodachiLifeMask mask)
    {
        var templateWindow = new TemplateTool(mask);
        var templateOutput = await templateWindow.ShowDialog<TemplateToolResponse?>(this);
        if (templateOutput != null)
        {
            if (templateOutput.Success && templateOutput.Result != null)
            {
                LoadImageFromBitmap(templateOutput.Result, $"template_{mask}.png");
                AppendLog($"Loaded masked image for template {mask.GetDescription()} from editor.");
            }
            else if (templateOutput.CouldNotLoad)
            {
                AppendLog(
                    $"Template editor failed to load the template for {mask.GetDescription()}"
                );
                _ = ShowMessageAsync(
                    "Error loading template",
                    "The template tool could not find the image. This REALLY shouldn't happen... Try reinstalling?"
                );
            }
            else
            {
                AppendLog($"Template editor closed with no input. Nothing changed.");
            }
        }
        else
        {
            AppendLog($"The template editor closed unexpectedly...");
        }
    }

    private async void MainWindow_Opened(object? sender, EventArgs e)
    {
        if (_currentSettings.FirstStartId != CURRENT_WELCOME_ID)
        {
            ShowWelcomeMessage();
            _currentSettings.FirstStartId = CURRENT_WELCOME_ID;
        }

#if DEBUG
        InsertDebugMenuItems();
#endif
        SaveSettings();

        if (!IsVCRuntimeInstalled())
        {
            await ShowMessageAsync(
                "WARNING: MISSING LIBRARIES",
                $"In order for this program to run, you MUST install the VC Redistributable."
                    + $"\n\nClick the open link button to install it. "
                    + $"If you do not install it, this program will probably crash silently.",
                new Uri("https://aka.ms/vc14/vc_redist.x64.exe"),
                "Download Redistributable"
            );
        }
    }

    // Welcome message stuff. For important changes, the ID is incremented by one by hand whenever something notable changes.
    // This is only really needed for Mac since its settings are saved in a way that persists more readily.
    private const int CURRENT_WELCOME_ID = 3;

    private async void ShowWelcomeMessage()
    {
        await ShowMessageAsync(
            "Welcome to TomodachiDrawer V2",
            "This is TomodachiDrawer V2 — a fork by @xoaninc that adds ESP32-S3 support "
                + "(alongside the original RP2040) plus fixes, based on the original by @Lucas7yoshi.\n\n"
                + "New: the left panel has an \"ESP32-S3 Output\" section to flash an ESP32-S3 and "
                + "draw on your Switch. Press \"Setup Steps (ESP32)\" there for a full guide.\n\n"
                + "Free software under GPL-3.0; the original project's credit is preserved. "
                + "Use Help → Open GitHub Repo for this V2 project."
        );
    }

    private static string GetVersionString(bool includeCommit)
    {
        var currentVersion =
            Assembly
                .GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion
            ?? "dev";
        if (currentVersion.StartsWith("0.0.0"))
        {
            if (includeCommit)
            {
                return "dev+" + currentVersion.Split('+').Last();
            }
            else
            {
                return "dev";
            }
        }
        if (!includeCommit)
        {
            return currentVersion.Split('+').First();
        }
        return currentVersion;
    }

#if DEBUG
    private void InsertDebugMenuItems()
    {
        var debugMenuItem = new MenuItem() { Header = "_Debug" };
        Menu.Items.Add(debugMenuItem);

        MenuDebugConnectVirtualGamepad = new MenuItem() { Header = "_Connect Virtual Gamepad" };
        MenuDebugConnectVirtualGamepad.Click += MenuDebugConnectVirtualGamepad_Click;
        debugMenuItem.Items.Add(MenuDebugConnectVirtualGamepad);

        MenuDebugRunInVirtualGamepad = new MenuItem()
        {
            Header = "_Run in Virtual Gamepad",
            IsEnabled = false,
        };
        MenuDebugRunInVirtualGamepad.Click += MenuDebugRunInVirtualGamepad_Click;
        debugMenuItem.Items.Add(MenuDebugRunInVirtualGamepad);

        MenuDebugOpenVirtualGamepadController = new MenuItem()
        {
            Header = "_Control Virtual Gamepad",
            IsEnabled = false,
        };
        MenuDebugOpenVirtualGamepadController.Click += MenuDebugOpenVirtualGamepadController_Click;
        debugMenuItem.Items.Add(MenuDebugOpenVirtualGamepadController);
    }
#endif

    private async Task PerformAsyncUpdateCheck()
    {
        try
        {
            var ourVersion = GetVersionString(false);
            if (ourVersion == "dev")
            {
                AppendLog("Skipping update check for dev.");
                return;
            }
            using var http = new HttpClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"TomodachiDrawer {ourVersion}");

            using var response = await http.GetAsync(
                "https://api.github.com/repos/xoaninc/TomodachiDrawer-V2/releases/latest"
            );
            response.EnsureSuccessStatusCode();
            using var responseStream = await response.Content.ReadAsStreamAsync();

            using var responseJsonObject = JsonDocument.Parse(responseStream);

            // 0.0.0 format, no v, no -.
            var releaseVersionTag =
                responseJsonObject.RootElement.GetProperty("tag_name").GetString() ?? "0.0.0";

            // see if its newer. TODO: Actually check that, only really effects using the artifacts from the release build before
            // i've published the release though.
            if (releaseVersionTag != null)
            {
                if (releaseVersionTag != ourVersion)
                {
                    _ = ShowMessageAsync(
                        "Update available",
                        "A new update is available on GitHub."
                            + $"\nCurrent Version: {ourVersion}"
                            + $"\nLatest Version: {releaseVersionTag}"
                            + $"\nVersion title: {responseJsonObject.RootElement.GetProperty("name").GetString() ?? "N/A"}"
                            + $"\n\nDownload at:\nhttps://github.com/xoaninc/TomodachiDrawer-V2",
                        new Uri("https://github.com/xoaninc/TomodachiDrawer-V2/releases"),
                        "Open Releases"
                    );
                }
                else
                {
                    AppendLog($"Up to date! {ourVersion}");
                }
            }
        }
        catch (Exception ex)
        {
            AppendLog($"Failed to check for updates: {ex.Message}");
        }
    }

    protected override void OnClosed(System.EventArgs e)
    {
        _cts.Cancel();
        base.OnClosed(e);
    }

    /// <summary>
    /// The macOS permission-denied dialog, with a deep link into the right Settings pane and the
    /// manual-copy escape hatch. Extracted so both the pre-check and the write itself can show it —
    /// upstream found in the field that passing the pre-check does not guarantee the write succeeds.
    /// </summary>
    /// <param name="retryBlurb">Set when the caller will retry after the dialog closes.</param>
    private Task ShowMacAccessError(string drivePath, string driveName, bool retryBlurb = false)
    {
        var additional = retryBlurb
            ? "\n\nGrant the permission, then click OK and the app will try to write again."
            : string.Empty;

        return ShowMessageAsync(
            "Permission Denied",
            $"Permission to access the {driveName} drive ({drivePath}) was denied.\n\n"
                + "Please open System Settings -> Privacy & Security -> Files & Folders, find \"TomodachiDrawer\", and make sure \"Removable Volumes\" is enabled.\n\n"
                + $"This is required for the app to write directly to your {driveName} drive.\r"
                + $"Or you can manually copy the .uf2 file to {drivePath} if you want to avoid granting permissions."
                + additional,
            new Uri(
                "x-apple.systempreferences:com.apple.preference.security?Privacy_FilesAndFolders"
            ),
            "Open System Settings"
        );
    }

    // Check if we can access the microcontroller's drive.
    // Also triggers the permission prompt on macOS if we haven't been granted permissions yet.
    // Returns `true` if we can access it.
    //
    // NOTE: a `true` here is not a guarantee the subsequent write will succeed — upstream saw
    // exactly that happen in the field. Callers must still guard the write.
    private bool CanAccessPicoDrive(string drivePath, string driveName = "RPI-RP2")
    {
        try
        {
            // Try to access the drive by listing its files.
            // This also trigger the permission prompt on macOS.
            _ = Directory.GetFiles(drivePath);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // macOS: User (probably) clicked "Don't Allow".
            if (OperatingSystem.IsMacOS())
                _ = ShowMacAccessError(drivePath, driveName);

            // Log the error. Just in case, log on other OSes as well.
            AppendLog($"Permission to access {driveName} drive ({drivePath}) was denied");
            return false;
        }
        catch (Exception ex)
        {
            // Also just in case, log any other error that might occur while trying to access the drive.
            AppendLog($"Could not access the {driveName} drive ({drivePath}): {ex.Message}");
            return false;
        }
    }

    // ── RP2040 polling ────────────────────────────────────────────────

    private void StartPicoPolling()
    {
        _ = Task.Run(async () =>
        {
            bool lastRp2040 = false;
            bool lastRp2350 = false;
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var rp2040Path = UF2Flasher.FindRP2040Drive();
                    var rp2350Path = UF2Flasher.FindRP2350Drive();
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        bool hasImage = _currentImage != null;

                        // Estimate only needs an image — no device required
                        EstimateButton.IsEnabled = hasImage && !BusyExporting;
                        // .tdld needs no microcontroller — an image is enough.
                        ExportTDLDButton.IsEnabled = hasImage && !BusyExporting;

                        lastRp2040 = UpdateChipUI(
                            RPChipType.RP2040,
                            rp2040Path,
                            hasImage,
                            lastRp2040
                        );
                        lastRp2350 = UpdateChipUI(
                            RPChipType.RP2350,
                            rp2350Path,
                            hasImage,
                            lastRp2350
                        );
                    });

                    await Task.Delay(1000, _cts.Token);
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
                catch
                {
                    // Transient device-enumeration / UI error — keep polling rather than letting
                    // the loop die and freezing device detection for the rest of the session.
                }
            }
        });
    }

    // Refreshes one Pico tab's status label + buttons. Returns the new "connected" state so the
    // caller can detect connect/disconnect transitions for logging.
    private bool UpdateChipUI(RPChipType chip, string? path, bool hasImage, bool last)
    {
        var (statusLabel, flashButton, exportButton, exportUf2Button, chipName) =
            chip == RPChipType.RP2350
                ? (
                    RP2350StatusLabel,
                    RP2350FlashButton,
                    RP2350ExportButton,
                    RP2350ExportUF2Button,
                    "RP2350"
                )
                : (
                    RP2040StatusLabel,
                    RP2040FlashButton,
                    RP2040ExportButton,
                    RP2040ExportUF2Button,
                    "RP2040"
                );

        // Export-to-.UF2 only needs an image — no device required.
        exportUf2Button.IsEnabled = hasImage && !BusyExporting;

        if (path != null)
        {
            statusLabel.Text = $"{chipName} found: {path}";
            statusLabel.Foreground = Brushes.Green;
            flashButton.IsEnabled = !BusyExporting;
            exportButton.IsEnabled = hasImage && !BusyExporting;
            if (!last)
                AppendLog($"{chipName} connected @ {path}");
            return true;
        }
        else
        {
            statusLabel.Text = $"{chipName} not found";
            statusLabel.Foreground = Brushes.Red;
            flashButton.IsEnabled = false;
            exportButton.IsEnabled = false;
            if (last)
                AppendLog($"{chipName} disconnected...");
            return false;
        }
    }

    // ── ESP32-S3 ──────────────────────────────────────────────────────

    private const string esp32FirmwareFileName = "TomodachiDrawer.Firmware.ESP32.bin";
    private string[] _esp32Ports = Array.Empty<string>();

    private static string GetEsp32FirmwareFilePath()
    {
        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS() && baseDirectory.Contains(".app/Contents/MacOS"))
            return Path.Combine(baseDirectory, esp32FirmwareFileName);
        return esp32FirmwareFileName;
    }

    // Unlike the RP2040 (which mounts as a drive in BOOT mode), the ESP32-S3
    // exposes a serial port only while in download mode. We list serial ports
    // and let the user pick; the chip type is validated at flash time.
    private void StartEsp32Polling()
    {
        _ = Task.Run(async () =>
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                string[] ports;
                try
                {
                    ports = SerialPort.GetPortNames().Distinct().OrderBy(p => p).ToArray();
                }
                catch
                {
                    ports = Array.Empty<string>();
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    bool hasImage = _currentImage != null;

                    if (!_esp32Ports.SequenceEqual(ports))
                    {
                        var previous = ESP32PortComboBox.SelectedItem as string;
                        _esp32Ports = ports;
                        ESP32PortComboBox.ItemsSource = ports;
                        if (previous != null && ports.Contains(previous))
                            ESP32PortComboBox.SelectedItem = previous;
                        else if (ports.Length > 0)
                            ESP32PortComboBox.SelectedIndex = 0;
                    }

                    bool portSelected = ESP32PortComboBox.SelectedItem is string;
                    if (ports.Length > 0)
                    {
                        ESP32StatusLabel.Text =
                            $"ESP32: {ports.Length} serial port(s) — pick the one in download mode";
                        ESP32StatusLabel.Foreground = Brushes.Green;
                    }
                    else
                    {
                        ESP32StatusLabel.Text =
                            "ESP32 not found — hold BOOT, tap RESET, release BOOT";
                        ESP32StatusLabel.Foreground = Brushes.Red;
                    }

                    FlashEsp32FirmwareButton.IsEnabled = portSelected && !BusyExporting;
                    ExportEsp32Button.IsEnabled = portSelected && hasImage && !BusyExporting;
                });

                try
                {
                    await Task.Delay(1000, _cts.Token);
                }
                catch (System.OperationCanceledException)
                {
                    break;
                }
            }
        });
    }

    private async void FlashEsp32FirmwareButton_Click(object? sender, RoutedEventArgs e)
    {
        if (ESP32PortComboBox.SelectedItem is not string port)
        {
            _ = ShowMessageAsync(
                "ESP32",
                "Select the ESP32 serial port first. The board must be in download mode (hold BOOT, tap RESET, release BOOT)."
            );
            return;
        }

        var firmwarePath = GetEsp32FirmwareFilePath();
        if (!File.Exists(firmwarePath))
        {
            _ = ShowMessageAsync(
                "Error flashing ESP32 base firmware",
                $"Could not locate {esp32FirmwareFileName}.\nMake sure you run the app from its extracted folder."
            );
            return;
        }
        var bytes = File.ReadAllBytes(firmwarePath);

        BusyExporting = true;
        FlashEsp32FirmwareButton.IsEnabled = false;
        ExportEsp32Button.IsEnabled = false;
        AppendLog($"Flashing ESP32-S3 base firmware via {port} ...");

        string? error = null;
        bool cancelled = false;
        await Task.Run(async () =>
        {
            try
            {
                await EspFlasher.FlashBaseFirmwareAsync(port, bytes, null, _cts.Token, AppendLog);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });

        BusyExporting = false;
        EndGeneration();
        if (cancelled)
        {
            AppendLog("Generation cancelled.");
        }
        else if (error != null)
        {
            AppendLog($"ESP32 base firmware flash failed: {error}");
            _ = ShowMessageAsync("ESP32 flash failed", error);
        }
        else
        {
            AppendLog(
                "Flashed ESP32-S3 base firmware. Board is still in download mode — you can Export now.\r\n"
            );
            _ = ShowMessageAsync(
                "Done",
                "ESP32-S3 base firmware flashed!\n\n"
                    + "Do NOT press RESET now — the board is still in download mode, ready to flash your drawing.\n\n"
                    + "Next: load your image, choose your Switch version, and press \"Export To ESP32!\".\n\n"
                    + "(If you reset now the firmware starts running as the controller and the serial port "
                    + "disappears, so you'd have to re-enter download mode to flash again.)"
            );
        }
    }

    private async void ExportEsp32Button_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;

        if (ESP32PortComboBox.SelectedItem is not string port)
        {
            _ = ShowMessageAsync(
                "ESP32",
                "Select the ESP32 serial port first. The board must be in download mode (hold BOOT, tap RESET, release BOOT)."
            );
            return;
        }

        if (_currentSettings.SelectedSwitchVersion == SwitchVersion.None)
        {
            _ = ShowMessageAsync(
                "Select Switch Version",
                "For compatibility, you must select a switch version in the dropdown."
            );
            return;
        }

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        BusyExporting = true;
        ExportEsp32Button.IsEnabled = false;
        TimeSpan totalTime = TimeSpan.MaxValue;
        string? error = null;
        bool cancelled = false;

        await Task.Run(async () =>
        {
            try
            {
                using var img = imageSnapshot;
                var (tdldBytes, time) = await GetTdldAsync(
                    img,
                    drawSettings,
                    _currentSettings.SelectedSwitchVersion,
                    token
                );
                totalTime = time;
                AppendLog(
                    $"Flashing {tdldBytes.Length} bytes to the ESP32-S3 tdld partition via {port} ..."
                );
                await EspFlasher.FlashTdldAsync(port, tdldBytes, null, _cts.Token, AppendLog);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });

        BusyExporting = false;
        EndGeneration();
        ExportEsp32Button.IsEnabled = true;

        if (cancelled)
        {
            AppendLog("Generation cancelled.");
        }
        else if (error != null)
        {
            AppendLog($"ESP32 export failed: {error}");
            _ = ShowMessageAsync("ESP32 export failed", error);
        }
        else
        {
            SetEstimate(totalTime);
            _ = ShowMessageAsync(
                "Done",
                "Drawing data flashed to the ESP32-S3!\n\n"
                    + "Now RESET to start it (this is the moment to reset):\n"
                    + "1. Unplug it from the PC and plug the port labelled USB on the DevKitC-1 (not UART) "
                    + "into your Switch — connecting power boots it and it starts drawing automatically.\n"
                    + "   (Or tap RESET on the board to run it right now, e.g. to check the LED.)\n\n"
                    + "2. On the Switch: enable \"Wired Pro Controller Communication\", open Palette House "
                    + "(advanced UI), cursor at the top-left, zoomed out, top colour black."
            );
        }
    }

    #region Image/Preview
    private void LoadImage(string path)
    {
        if (!File.Exists(path))
        {
            AppendLog($"File does not exist..? {path}");
            return;
        }

        var img = SKBitmap.Decode(path);
        if (img == null)
        {
            AppendLog($"Failed to decode image: {path}");
            return;
        }

        if (img.Width > 256 || img.Height > 256)
        {
            float scale = Math.Min(256f / img.Width, 256f / img.Height);
            int newWidth = (int)(img.Width * scale);
            int newHeight = (int)(img.Height * scale);

            var resized = img.Resize(
                new SKImageInfo(newWidth, newHeight),
                new SKSamplingOptions(SKCubicResampler.CatmullRom)
            );
            img = resized;
            AppendLog($"Image resized to {newWidth}x{newHeight}");
        }

        LoadImageFromBitmap(img, Path.GetFileName(path));
    }

    /// <summary>
    /// Stores <paramref name="img"/> as the active image and refreshes all dependent UI.
    /// Takes ownership of <paramref name="img"/> — do not dispose it after calling this.
    /// </summary>
    private void LoadImageFromBitmap(SKBitmap img, string displayName)
    {
        _currentImage?.Dispose();
        _currentImage = img;
        _currentImagePath = displayName; // kept for log messages / ImagePathBox

        ImagePathBox.Text = displayName;
        RP2040ExportUF2Button.IsEnabled = true;
        RP2350ExportUF2Button.IsEnabled = true;

        if (img.Width == 256 && img.Height == 256)
        {
            AppendLog(
                "Image is full canvas size, so enabling auto home by default.\nYou can disable it if it causes you trouble and manually home before connecting."
            );
            EnableHomeCanvas.IsChecked = true;
        }

        UpdatePreview();
        TSPTimeLimitUpDown.Value = (decimal)
            CanvasDrawer.GetRecommendedTSPSolveTime(img.Width, img.Height);
        AppendLog($"Loaded image: {displayName} ({img.Width}x{img.Height})");
    }

    private SKBitmap GetPreview()
    {
        if (_currentImage == null)
            throw new InvalidOperationException("No image loaded.");

        var pal = new ColourPalette(new DummySink());
        var denoiser = DenoisingComboBox.SelectedItem?.ToString();
        var quantizerSettings = GetQuantizerSettings();
        return pal.PreviewColourMapping(_currentImage, quantizerSettings, denoiser);
    }

    private void UpdatePreview()
    {
        if (_currentImage == null)
        {
            AppendLog($"No image loaded, cannot update preview.");
            return;
        }

        var quantizerSettings = GetQuantizerSettings();
        var preview = GetPreview();

        PreviewImage.Source = ToAvaloniaBitmap(preview);
        // Size in the header, purely so the user can sanity-check what got loaded/resized.
        PreviewHeader.Text = $"Preview ({_currentImage.Width}x{_currentImage.Height})";
        AppendLog(
            $"Updated preview for {_currentImagePath} using {quantizerSettings.quantizerName}"
        );
    }

    public static Bitmap ToAvaloniaBitmap(SKBitmap skBitmap)
    {
        using var image = SKImage.FromBitmap(skBitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = new MemoryStream(data.ToArray());
        return new Bitmap(stream);
    }
    #endregion

    private void AppendLog(string msg)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogBox.Text = (LogBox.Text ?? "") + msg + "\n";
            LogBox.CaretIndex = LogBox.Text?.Length ?? 0;
        });
    }

    // messagebox replacement
    private async Task ShowMessageAsync(
        string title,
        string message,
        Uri? link = null,
        string? linkButtonText = null
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

        Button? linkButton = null;

        if (link != null)
        {
            linkButton = new Button
            {
                Content = linkButtonText ?? "Open Link",
                Margin = new Thickness(0, 10, 0, 0),
                MinWidth = 80,
            };
            buttonRow.Children.Add(linkButton);
        }

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
        linkButton?.Click += (_, _) =>
        {
            // Link button is only non-null if link is non-null so ! to indicate its safe.
            Launcher.LaunchUriAsync(link!);
        };
        await dialog.ShowDialog(this);
    }

    private async void OpenImageButton_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Open Image",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Images")
                    {
                        Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.gif", "*.webp"],
                    },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            }
        );

        if (files.Count > 0)
            LoadImage(files[0].TryGetLocalPath() ?? "");
    }

    private void ColourMatcherComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_currentImage != null)
            UpdatePreview();
        ColourLimitUpDown.IsEnabled =
            ColourMatcherComboBox?.SelectedValue?.ToString() == "Arbitrary";
        DrawingOptionChanged();
    }

    private void TSPHelpButton_Click(object? sender, RoutedEventArgs e)
    {
        const string message =
            "TSP Solver Time Limit refers to how much time is alloted to the TSP solver.\n"
            + "TSP refers to the Travelling Sales Person problem, which is finding the optimal route among a set of points.\n"
            + "This is used to find the optimal path for the pen tool to take while drawing to minimize drawing time.\n\n"
            + "For larger images, the TSP solver can take longer to find an optimal route, its also possible it will never even find an optimal route if there is too many points.\n"
            + "For 64x64, 0.5s is generally fine, anything largest you should consider giving it more time.\n\n"
            + "This time is how long it is alloted PER colour, so if an image has 30 different colours used, 0.5s will take 15 seconds.\n"
            + "The TSP solve is not used always, a simpler \"snaking\" algorithm is used if its quicker, or if TSP didnt find anything in time, which it sometimes is, mostly for large continuous areas of colour.";

        _ = ShowMessageAsync("TSP Solver Time Limit", message);
    }

    private QuantizerSettings GetQuantizerSettings()
    {
        string quantizerName = ColourMatcherComboBox.SelectedItem!.ToString()!;
        if (quantizerName == "Arbitrary")
        {
            var colourCount = (int)(ColourLimitUpDown.Value ?? 32);
            return new QuantizerSettings(quantizerName, colourCount, default);
        }
        return new QuantizerSettings(quantizerName, default, default);
    }

    private async void ExportToDeviceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;

        if (_currentSettings.SelectedSwitchVersion == SwitchVersion.None)
        {
            _ = ShowMessageAsync(
                "Select Switch Version",
                "For compatibility, you must select a switch version in the dropdown."
                    + "\n\nSwitch 1 is more prone to desyncs, so this avoids certain things that are particularly prone to desyncing."
                    + "\nPlease be aware that even with Switch 1 selected, desyncs are unfortunately expected due to inconsistent and unpredictable lag in the drawing UI."
            );
            return;
        }

        var chip = sender == RP2350ExportButton ? RPChipType.RP2350 : RPChipType.RP2040;
        var exportButton = chip == RPChipType.RP2350 ? RP2350ExportButton : RP2040ExportButton;

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        BusyExporting = true;
        exportButton.IsEnabled = false;
        TimeSpan totalTime = TimeSpan.MaxValue;

        // try/finally so a failure during generation/flash never leaves BusyExporting stuck true
        // (which would lock every export/estimate button for the rest of the session).
        try
        {
            await Task.Run(async () =>
            {
                using var img = imageSnapshot;
                var (tdldBytes, time) = await GetTdldAsync(
                    img,
                    drawSettings,
                    _currentSettings.SelectedSwitchVersion,
                    token
                );
                totalTime = time;

                var uf2Bytes = UF2Flasher.BuildTDLDUF2(tdldBytes, chip);
                var drivePath = UF2Flasher.FindDriveForChip(chip);
                var driveName = chip == RPChipType.RP2350 ? "RP2350" : "RPI-RP2";

                if (uf2Bytes == null || uf2Bytes.Length == 0)
                {
                    // Previously this fell through silently: the user waited out a whole generation
                    // and got no log, no dialog, nothing.
                    AppendLog($"{chip} export failed: produced an empty UF2.");
                    _ = ShowMessageAsync(
                        "Export failed",
                        "The UF2 came out empty, so nothing was written. This shouldn't happen — "
                            + "please report it with the log."
                    );
                }
                else if (drivePath == null)
                {
                    AppendLog(
                        $"{chip} export failed: the drive disappeared between detection and writing."
                    );
                    _ = ShowMessageAsync(
                        "Device disconnected",
                        $"The {chip} was detected when you pressed the button but is gone now, so "
                            + "nothing was written.\n\nReconnect it in BOOT mode and try again — the "
                            + "generated route is cached, so this will be quick."
                    );
                }
                else if (!CanAccessPicoDrive(drivePath, driveName))
                {
                    // CanAccessPicoDrive already logged and, on macOS, explained how to fix it.
                    AppendLog($"{chip} export aborted: the {driveName} drive is not accessible.");
                }
                else
                {
                    await WriteUf2WithRetryAsync(drivePath, driveName, uf2Bytes, chip);
                }
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("Generation cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"{chip} export failed: {ex.Message}");
        }
        finally
        {
            BusyExporting = false;
            EndGeneration();
            exportButton.IsEnabled = true;
        }

        SetEstimate(totalTime);
    }

    private DrawImageSettings GetDrawImageSettings()
    {
        var denoiser = DenoisingComboBox.SelectedItem?.ToString();
        var tspLimit = (float)(TSPTimeLimitUpDown.Value ?? 0.5m);
        var quantizerSettings = GetQuantizerSettings();
        var enableExperimental = EnableExperimentalMenuItem.IsChecked;
        var enableHome = EnableHomeCanvas.IsChecked ?? false;

        return new()
        {
            QuantizerSettings = quantizerSettings,
            DenoiserName = denoiser,
            TSPTimeLimit = tspLimit,
            DisableLargeBrush = false,
            EnableExperimentalFeatures = enableExperimental,
            HomeToTopLeft = enableHome,
            ReverseColourOrder = ReverseColourOrderCheckBox.IsChecked ?? false,
            EarlyTspExitEnabled = _currentSettings.EarlyTspExitEnabled,
            EarlyTspExitRateCoefficient = _currentSettings.EarlyTspExitRateCoefficient,
            EarlyTspExitSolutionsDistance = _currentSettings.EarlyTspExitSolutionsDistance,
        };
    }

    /// <summary>
    /// Starts a cancellable generation and enables the Cancel button. Any previous source is
    /// disposed — generations are serialised by BusyExporting, so there is never more than one.
    /// </summary>
    private CancellationToken BeginGeneration()
    {
        _generationCts?.Dispose();
        _generationCts = new CancellationTokenSource();
        CancelDrawButton.IsEnabled = true;
        return _generationCts.Token;
    }

    private void EndGeneration()
    {
        CancelDrawButton.IsEnabled = false;
        _generationCts?.Dispose();
        _generationCts = null;
    }

    private void CancelDrawButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_generationCts is { IsCancellationRequested: false })
        {
            AppendLog("Cancelling route generation…");
            _generationCts.Cancel();
            CancelDrawButton.IsEnabled = false;
        }
    }

    /// <summary>
    /// Saves the raw .tdld. Ported from upstream a1732a9 but in its post-725faf2 form: the original
    /// gated on `string.IsNullOrEmpty(_currentImagePath)` and re-decoded the file from disk, which
    /// is the NRE that 725faf2 later fixed. V2 snapshots _currentImage instead, like every other
    /// export path here.
    /// </summary>
    private async void ExportTDLDButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;

        if (_currentSettings.SelectedSwitchVersion == SwitchVersion.None)
        {
            _ = ShowMessageAsync(
                "Select Switch Version",
                "For compatibility, you must select a switch version in the dropdown."
                    + "\n\nSwitch 1 is more prone to desyncs, so this avoids certain things that are particularly prone to desyncing."
            );
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save .tdld",
                DefaultExtension = "tdld",
                FileTypeChoices =
                [
                    new FilePickerFileType("TDLD input stream") { Patterns = ["*.tdld"] },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            }
        );

        var outputPath = file?.TryGetLocalPath();
        if (outputPath == null)
            return;

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        ExportTDLDButton.IsEnabled = false;
        BusyExporting = true;
        TimeSpan totalTime = TimeSpan.MaxValue;

        try
        {
            await Task.Run(async () =>
            {
                using var img = imageSnapshot;
                var (tdldBytes, time) = await GetTdldAsync(
                    img,
                    drawSettings,
                    _currentSettings.SelectedSwitchVersion,
                    token
                );
                totalTime = time;
                File.WriteAllBytes(outputPath, tdldBytes);
                AppendLog($"Saved {tdldBytes.Length} bytes of .tdld to {outputPath}");
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("Generation cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($".tdld export failed: {ex.Message}");
        }
        finally
        {
            BusyExporting = false;
            EndGeneration();
            ExportTDLDButton.IsEnabled = _currentImage != null;
        }

        SetEstimate(totalTime);
    }

    /// <summary>
    /// Writes the UF2, and on a permission/IO failure explains it and retries <b>once</b> after the
    /// user has had the chance to grant access.
    /// <para>
    /// This exists because <see cref="CanAccessPicoDrive"/> returning true is not a guarantee —
    /// upstream discovered from crash reports that the write can still be denied right after the
    /// pre-check passes. Without this the failure surfaced as a bare
    /// "Access to the path is denied", with no guidance and no second chance.
    /// </para>
    /// </summary>
    private async Task WriteUf2WithRetryAsync(
        string drivePath,
        string driveName,
        byte[] uf2Bytes,
        RPChipType chip
    )
    {
        var target = Path.Combine(drivePath, "tdld_image.uf2");

        for (int attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                File.WriteAllBytes(target, uf2Bytes);
                AppendLog(
                    $"Wrote to {chip} flash. Unplug the {chip} and plug it into the switch without holding any button."
                );
                return;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                bool lastAttempt = attempt == 2;
                AppendLog(
                    $"Writing to {target} failed ({ex.GetType().Name}: {ex.Message})"
                        + (lastAttempt ? "." : " — asking for access and retrying once.")
                );

                if (lastAttempt)
                {
                    _ = ShowMessageAsync(
                        "Write failed",
                        $"Could not write to {target}.\n\n{ex.Message}\n\n"
                            + "You can still use \"Export to .UF2\" and copy the file to the drive by hand."
                    );
                    return;
                }

                if (OperatingSystem.IsMacOS())
                    await ShowMacAccessError(drivePath, driveName, retryBlurb: true);
                else
                    await ShowMessageAsync(
                        "Write failed",
                        $"Could not write to {target}.\n\n{ex.Message}\n\n"
                            + "Close anything that might be using the drive, then click OK to retry."
                    );
            }
        }
    }

    private void SetEstimate(TimeSpan time)
    {
        var estimateStr = $"{time:h\\hm\\ms\\s}";
        DrawTimeLabel.Text = $"Draw Time Estimate: {estimateStr}";
    }

    // Moved to Core (RouteFingerprint) so the "does every setting invalidate the cache?" property
    // can be tested — see FingerprintTests. Keeping a thin wrapper so call sites read the same.
    private static string ComputeFingerprint(
        SKBitmap img,
        DrawImageSettings settings,
        SwitchVersion ver
    ) => RouteFingerprint.Compute(img, settings, ver);

    // Generates the .tdld for the given image/settings, OR returns the cached one
    // if the inputs are byte-for-byte identical to the last generation. Runs the
    // (slow) route generation only on a cache miss. Call from a background thread.
    private async Task<(byte[] tdld, TimeSpan time)> GetTdldAsync(
        SKBitmap img,
        DrawImageSettings settings,
        SwitchVersion ver,
        CancellationToken cancellationToken = default
    )
    {
        var fingerprint = ComputeFingerprint(img, settings, ver);
        lock (_cacheLock)
        {
            if (fingerprint == _cachedFingerprint && _cachedTdld != null)
            {
                AppendLog(
                    "Reusing cached route (image and settings unchanged) — no re-generation needed."
                );
                return (_cachedTdld, _cachedTime);
            }
        }

        var tempPath = Path.Combine(
            Path.GetTempPath(),
            $"tdld{System.Random.Shared.Next(1000000, 9999999)}.tdld"
        );
        var timingSink = new TimingSink();
        // Production: solve the independent per-layer TSPs in parallel across CPU cores (~6-7x
        // faster generation). The emitted .tdld is equivalent (route order differs, coverage same).
        var drawer = new CanvasDrawer(timingSink, ver, AppendLog, parallelSolves: true);
        drawer.ConnectAndConfirmController();
        await drawer.DrawImage(img, settings, cancellationToken);

        var fileSink = new FileControllerSink(tempPath);
        timingSink.ReplayTo(fileSink);
        fileSink.Dispose();
        var bytes = File.ReadAllBytes(tempPath);
        try
        {
            File.Delete(tempPath);
        }
        catch
        { /* best effort */
        }

        lock (_cacheLock)
        {
            _cachedTdld = bytes;
            _cachedTime = timingSink.TotalTime;
            _cachedFingerprint = fingerprint;
        }
        return (bytes, timingSink.TotalTime);
    }

    // Computes the draw-time estimate WITHOUT flashing, so it can be seen before
    // committing to an export. Runs the same route generation an export does.
    private async void EstimateButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;

        if (_currentSettings.SelectedSwitchVersion == SwitchVersion.None)
        {
            _ = ShowMessageAsync(
                "Select Switch Version",
                "For an accurate estimate, select a Switch version first (it affects the generated route)."
            );
            return;
        }

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        BusyExporting = true;
        EstimateButton.IsEnabled = false;
        DrawTimeLabel.Text = "Draw Time Estimate: estimating…";
        TimeSpan totalTime = TimeSpan.MaxValue;
        string? error = null;
        bool cancelled = false;

        await Task.Run(async () =>
        {
            try
            {
                using var img = imageSnapshot;
                var (_, time) = await GetTdldAsync(
                    img,
                    drawSettings,
                    _currentSettings.SelectedSwitchVersion,
                    token
                );
                totalTime = time;
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
            }
        });

        BusyExporting = false;
        EndGeneration();
        EstimateButton.IsEnabled = true;

        if (cancelled)
        {
            AppendLog("Generation cancelled.");
        }
        else if (error != null)
        {
            AppendLog($"Estimate failed: {error}");
            DrawTimeLabel.Text = "Draw Time Estimate: ???";
        }
        else
        {
            SetEstimate(totalTime);
        }
    }

    private async void ExportUF2Button_Click(object sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;

        if (_currentSettings.SelectedSwitchVersion == SwitchVersion.None)
        {
            _ = ShowMessageAsync(
                "Select Switch Version",
                "For compatibility, you must select a switch version in the dropdown."
                    + "\n\nSwitch 1 is more prone to desyncs, so this avoids certain things that are particularly prone to desyncing."
                    + "\nPlease be aware that even with Switch 1 selected, desyncs are unfortunately expected due to inconsistent and unpredictable lag in the drawing UI."
            );
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save .UF2",
                DefaultExtension = "uf2",
                FileTypeChoices =
                [
                    new FilePickerFileType("UF2 Firmware Image") { Patterns = ["*.uf2"] },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            }
        );

        var outputPath = file?.TryGetLocalPath();
        if (outputPath == null)
            return;

        var chip = sender == RP2350ExportUF2Button ? RPChipType.RP2350 : RPChipType.RP2040;
        var exportUf2Button =
            chip == RPChipType.RP2350 ? RP2350ExportUF2Button : RP2040ExportUF2Button;

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        exportUf2Button.IsEnabled = false;
        BusyExporting = true;
        TimeSpan totalTime = TimeSpan.MaxValue;

        // try/finally so a failure during generation never leaves BusyExporting stuck true
        // (which would lock every export/estimate button for the rest of the session).
        try
        {
            await Task.Run(async () =>
            {
                using var img = imageSnapshot;
                var (tdldBytes, time) = await GetTdldAsync(
                    img,
                    drawSettings,
                    _currentSettings.SelectedSwitchVersion,
                    token
                );
                totalTime = time;

                var uf2Bytes = UF2Flasher.BuildTDLDUF2(tdldBytes, chip);
                if (uf2Bytes != null && uf2Bytes.Length > 0)
                {
                    File.WriteAllBytes(outputPath, uf2Bytes);
                    AppendLog($"Saved UF2 to {outputPath}");
                }
            });
        }
        catch (OperationCanceledException)
        {
            AppendLog("Generation cancelled.");
        }
        catch (Exception ex)
        {
            AppendLog($"UF2 export failed: {ex.Message}");
        }
        finally
        {
            exportUf2Button.IsEnabled = true;
            BusyExporting = false;
            EndGeneration();
        }

        SetEstimate(totalTime);
    }

    private static string GetRPFirmwareFileName(RPChipType chip) =>
        chip == RPChipType.RP2350
            ? "TomodachiDrawer.Firmware.rp2350.uf2"
            : "TomodachiDrawer.Firmware.rp2040.uf2";

    private static string GetBaseFirmwareFilePath(RPChipType chip)
    {
        var firmwareFileName = GetRPFirmwareFileName(chip);
        // Check if we're running on macOS and the app is running from app bundle, not CLI.
        var baseDirectory = AppContext.BaseDirectory;
        if (OperatingSystem.IsMacOS() && baseDirectory.Contains(".app/Contents/MacOS"))
        {
            // In macOS, when you launch `.app` from Finder, the current working directory is root directory `/`,
            // and the firmware file isn't located there. `AppContext.BaseDirectory` resolves to
            // `/path/to/TomodachiDrawer.app/Contents/MacOS/`, where the firmware file lives.
            return Path.Combine(baseDirectory, firmwareFileName);
        }
        else
        {
            // Simply use the file in current working directory
            return firmwareFileName;
        }
    }

    private void FlashFirmwareButton_Click(object? sender, RoutedEventArgs e)
    {
        var chip = sender == RP2350FlashButton ? RPChipType.RP2350 : RPChipType.RP2040;
        var firmwareFileName = GetRPFirmwareFileName(chip);
        var firmwareFilePath = GetBaseFirmwareFilePath(chip);
        var drivePath = UF2Flasher.FindDriveForChip(chip);

        if (!File.Exists(firmwareFilePath))
        {
            _ = ShowMessageAsync(
                "Error flashing base firmware",
                $"For some reason could not locate {firmwareFileName}"
                    + "\nPlease ensure that you extracted the program to a folder, and ran the executable from that extracted folder."
                    + $"\nIf you still cannot flash with this button, you can manually drag the {firmwareFileName} file to the device's drive on your system to flash it."
            );
            return;
        }
        if (drivePath == null)
        {
            _ = ShowMessageAsync("Error", $"{chip} not detected. Connect it in BOOT mode first.");
            return;
        }
        if (!CanAccessPicoDrive(drivePath))
        {
            return;
        }

        File.Copy(firmwareFilePath, Path.Combine(drivePath, firmwareFileName), overwrite: true);

        var timeout = System.DateTime.Now.AddSeconds(10);
        while (UF2Flasher.FindDriveForChip(chip) != null)
        {
            if (System.DateTime.Now > timeout)
            {
                _ = ShowMessageAsync(
                    "Error flashing base firmware",
                    "Wrote file but expected it to reset itself by now, maybe try doing it manually..?"
                );
                return;
            }
            Thread.Sleep(500);
        }

        _ = ShowMessageAsync(
            "",
            "Base firmware flashed! You can now use the standard output button to output your images to it!\nIf this is your first time, its likely flashing red. Simply hold BOOT and plug it back in, or hold BOOT and press reset if you have it."
        );
        AppendLog($"Flashed base firmware to {chip}\r\n");
    }

    private void OutputExplanationButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ShowMessageAsync(
            "",
            "Your RP2040-Zero (or similar) needs two things in its memory (it's flash):\r\n"
                + "- The code that reads the instructions to draw your image and pipe it to the switch\r\n"
                + "- The instructions to draw your image.\r\n\r\n\r\n"
                + "To connect your device for flashing, hold down the \"BOOT\" button and plug it in, or hold \"BOOT\" and press \"RESET\" while it is connected.\r\n\r\n"
                + "You only need to flash the code/\"firmware\" once.\r\n\r\n"
                + "You then flash the image data onto it for each image, without needing to reflash the firmware.\r\n\r\n"
                + "When you first install the firmware, it'll reset itself, flash yellow 3 times, and then flash red.\r\n"
                + "Flashing red is expected, as that means it cannot find the image data.\r\n"
                + "Reconnect it using the same \"BOOT\" button steps as described above, load your image, and hit \"Export to RP2040\".\r\n\r\n"
                + "Again, it will reboot, but now you can unplug it and plug it into your switch.\r\n\r\n"
                + "YOU MUST HAVE \"Pro Controller Wired Commmunication\" ENABLED.\r\n"
                + "Go to system settings -> Controllers & Accessories -> Pro Controller Wired Communication\r\n"
        );
    }

    private void InGameSetupButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ShowMessageAsync(
            "In Game Setup",
            "Setup in game is fairly straightforward.\r\n"
                + "- Navigate to the palette house\r\n"
                + "- Ensure you are on the \"advanced\" drawing UI\r\n"
                + "- Ensure your top colour is set to Black (it is by default)\r\n"
                + "- Set your cursor to the TOP LEFT of where you want the drawing to be.\r\n"
                + "- Ensure the full area of the canvas that will be drawn is on screen.\r\n\r\n"
                + "If the canvas is zoomed in, it will cause the cursor to desync as the canvas moves when the cursor gets on the edges. Zooming out fully avoids this.\r\n\r\n"
                + "If your image is 256x256 or larger, set it all the way in the top left. If your image is smaller, set your cursor to where you want the topleft most pixel of your drawing to be."
        );
    }

    private void Esp32ExplanationButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ShowMessageAsync(
            "ESP32-S3 Setup Steps",
            "The ESP32-S3-DevKitC-1 has TWO USB-C ports:\r\n"
                + "- \"UART\": a USB-serial bridge, fine for flashing.\r\n"
                + "- \"USB\": the chip's native USB, used for flashing AND for the Switch.\r\n"
                + "You can do everything over the USB port. Use that one for the Switch.\r\n\r\n"
                + "DRIVERS:\r\n"
                + "- The native USB port needs NO driver on Windows 10/11 (plug and play).\r\n"
                + "- Only if no COM port shows up (or you use the UART port) install the\r\n"
                + "  USB-serial driver for your board's bridge chip:\r\n"
                + "  - Silicon Labs CP210x (CP2102): https://www.silabs.com/software-and-tools/usb-to-uart-bridge-vcp-drivers\r\n"
                + "  - WCH CH340/CH341: https://www.wch-ic.com/downloads/CH341SER_EXE.html\r\n\r\n"
                + "To enter DOWNLOAD MODE (needed for any flashing):\r\n"
                + "hold BOOT, tap RESET, then release BOOT.\r\n\r\n"
                + "KEY IDEA: you can only flash while in DOWNLOAD MODE (a COM port shows up).\r\n"
                + "Once the firmware runs it becomes the controller, the port disappears, and\r\n"
                + "flashing is disabled until you re-enter download mode. So do the flashing\r\n"
                + "first, and only reset/unplug at the very end.\r\n\r\n"
                + "FIRST TIME (firmware + image in one go):\r\n"
                + "1. Enter download mode, pick the COM port.\r\n"
                + "2. Press \"Flash Base Firmware (ESP32)\". When it says Done, do NOT reset.\r\n"
                + "3. Load your image, choose your Switch version, press \"Export To ESP32!\"\r\n"
                + "   (the board is still in download mode — no reset needed in between).\r\n"
                + "4. Unplug from the PC and plug the USB port into your Switch.\r\n\r\n"
                + "FOR LATER IMAGES (firmware already flashed):\r\n"
                + "- Enter download mode again, load the image, press \"Export To ESP32!\",\r\n"
                + "  then unplug to the Switch.\r\n\r\n"
                + "(If the LED gets stuck on solid yellow after a reset, tap RESET again — the\r\n"
                + " software reset doesn't always start the app; a manual reset does.)\r\n\r\n"
                + "LED meanings: dim white = idle, blinking yellow = startup countdown,\r\n"
                + "green = drawing (button held), red blink = no/invalid image data,\r\n"
                + "rainbow = finished.\r\n\r\n"
                + "YOU MUST HAVE \"Pro Controller Wired Communication\" ENABLED:\r\n"
                + "System Settings -> Controllers & Accessories -> Pro Controller Wired Communication."
        );
    }

    // this doesnt seem to work >:|
    // atleast on windows.

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = e.DataTransfer.Contains(DataFormat.File)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (!e.DataTransfer.Contains(DataFormat.File))
            return;
        var first = e.DataTransfer.TryGetFiles()?.FirstOrDefault();
        if (first != null)
            LoadImage(first.TryGetLocalPath() ?? "");
    }

    private void ColourLimitUpDown_ValueChanged(
        object? sender,
        NumericUpDownValueChangedEventArgs e
    ) => UpdatePreview();

    private void ThemeMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        int index =
            sender == ThemeLightMenuItem ? 1
            : sender == ThemeDarkMenuItem ? 2
            : 0;
        ThemeSystemMenuItem.IsChecked = index == 0;
        ThemeLightMenuItem.IsChecked = index == 1;
        ThemeDarkMenuItem.IsChecked = index == 2;
        SetTheme(index);
        SaveSettings();
    }

    private void SetTheme(int index)
    {
        var desiredTheme = index switch
        {
            1 => ThemeVariant.Light,
            2 => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = desiredTheme;
            _currentSettings.SelectedThemeIndex = index;
        }
    }

    private void ColourMatcherHelpButton_Click(object? sender, RoutedEventArgs e)
    {
        _ = ShowMessageAsync(
            "Colour Matchers",
            "You have 4 options for colour matchers."
                + "\nEuclidean, Redmean, and CieLab work using the Pro modes default palette."
                + "\n\nArbitrary on the other hands works using the full colour range, selecting colours in-game is slower but you can achieve much better results."
                + "\nYou can tweak the number of colours it has by changing the value to the right of this button."
                + "\nTry and pick the lowest number that looks good to your standards to minimize draw time."
                + "\nLess colours means quicker drawing, and more opportunities for the solver to find large continous blocks it can draw quickly."
                + "\nIf time is of the essence, you can also enable Denoising which can increase the number of large spots for the larger brushes."
        );
    }

    private void SaveSettings() => _currentSettings.Save();

    private void GetSettings()
    {
        _currentSettings = AppSettings.Load(out var warning);
        if (warning != null)
            AppendLog(warning);

        SwitchVersionComboBox.SelectedIndex = (int)_currentSettings.SelectedSwitchVersion - 1;
        SetTheme(_currentSettings.SelectedThemeIndex);
        ThemeSystemMenuItem.IsChecked = _currentSettings.SelectedThemeIndex == 0;
        ThemeLightMenuItem.IsChecked = _currentSettings.SelectedThemeIndex == 1;
        ThemeDarkMenuItem.IsChecked = _currentSettings.SelectedThemeIndex == 2;

        EnableExperimentalMenuItem.IsChecked = _currentSettings.EnableExperimentalFeatures;
        CheckForUpdatesCheckBox.IsChecked = _currentSettings.CheckForUpdatesOnStart;

        // Restore the drawing options. _loadingSettings suppresses the change handlers so
        // restoring a value cannot immediately write it back or kick off a preview rebuild.
        _loadingSettings = true;
        try
        {
            if (
                ColourMatcherComboBox.ItemsSource is IEnumerable<string> matchers
                && matchers.Contains(_currentSettings.ColourMatcherName)
            )
                ColourMatcherComboBox.SelectedItem = _currentSettings.ColourMatcherName;

            ColourLimitUpDown.Value = _currentSettings.ColourLimit;
            ColourLimitUpDown.IsEnabled =
                ColourMatcherComboBox?.SelectedValue?.ToString() == "Arbitrary";

            if (
                DenoisingComboBox.ItemsSource is IEnumerable<string> denoisers
                && denoisers.Contains(_currentSettings.DenoiserName)
            )
                DenoisingComboBox.SelectedItem = _currentSettings.DenoiserName;

            EnableHomeCanvas.IsChecked = _currentSettings.HomeToTopLeft;
            ReverseColourOrderCheckBox.IsChecked = _currentSettings.ReverseColourOrder;
        }
        finally
        {
            _loadingSettings = false;
        }
    }

    /// <summary>
    /// Set while restoring persisted settings into controls, so the SelectionChanged/Click handlers
    /// do not treat a restore as a user edit (which would save it straight back and rebuild the
    /// preview before an image is even loaded).
    /// </summary>
    private bool _loadingSettings;

    /// <summary>Persists whichever drawing option the user just changed.</summary>
    private void DrawingOptionChanged()
    {
        if (_loadingSettings)
            return;

        _currentSettings.ColourMatcherName =
            ColourMatcherComboBox.SelectedItem?.ToString() ?? "Arbitrary";
        _currentSettings.ColourLimit = (int)(ColourLimitUpDown.Value ?? 16);
        _currentSettings.DenoiserName = DenoisingComboBox.SelectedItem?.ToString() ?? "None";
        _currentSettings.HomeToTopLeft = EnableHomeCanvas.IsChecked ?? false;
        _currentSettings.ReverseColourOrder = ReverseColourOrderCheckBox.IsChecked ?? false;
        SaveSettings();
    }

    private void SwitchVersionComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (SwitchVersionComboBox.SelectedIndex == 0)
        {
            _currentSettings.SelectedSwitchVersion = SwitchVersion.Switch1;
            _ = ShowMessageAsync(
                "Switch 1 Warning",
                "Unfortunately the Switch 1 is significantly more prone to desyncing than the Switch 2."
                    + "\n\nOur leading theory as to why is that it is experiencing thermal issues whilst docked. The Switch 2 by comparison has a fan in its dock, the Switch 1 does not."
                    + "\nSome users have reported they could avoid the desyncs by limiting drawing to 45~ minutes or less, although the most successful method is seemingly to just undock the Switch and plug the microcontroller in directly."
                    + "\nHandheld runs at 1280x720 as opposed to 1920x1080 which can reduce the power draw, and being out of the dock it can get better airflow."
                    + "\nUnfortunately, this is still not a guarantee to avoid desyncs."
                    + "\n\nPlease keep this in mind when using the Switch 1 with this program."
            );
        }
        else
            _currentSettings.SelectedSwitchVersion = SwitchVersion.Switch2;
        SaveSettings();
    }

    private void EnableExperimentalMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (EnableExperimentalMenuItem.IsChecked)
        {
            _ = ShowMessageAsync(
                "Experimental Features",
                "WARNING: Enabling experimental features may induce more common desyncs. Things that are prone to desyncs, but that are desired to be made stable are put here."
                    + "\nNamely, this includes bucket filling dynamic areas on the switch 2."
                    + "\nOnly enable this if you are okay with the increased chance of desyncs. Having this disabled does not guarantee it will work, but that is the goal and in 99% of cases it will work.",
                new Uri("https://github.com/Lucas7yoshi/TomodachiDrawer/issues/34"),
                "Open Experimental Feature Info"
            );
        }
        _currentSettings.EnableExperimentalFeatures = EnableExperimentalMenuItem.IsChecked;
        SaveSettings();
    }

    private void CheckForUpdatesCheckBox_Click(object? sender, RoutedEventArgs e)
    {
        _currentSettings.CheckForUpdatesOnStart = CheckForUpdatesCheckBox.IsChecked;
        SaveSettings();
    }

    private async void MenuSavePreview_Click(object? sender, RoutedEventArgs e)
    {
        if (_currentImage == null)
            return;
        // very scientific
        var img = GetPreview();
        // save it to disk... wherever desired.
        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save preview .png",
                DefaultExtension = "png",
                FileTypeChoices =
                [
                    new FilePickerFileType("Portable Network Graphics Image")
                    {
                        Patterns = ["*.png"],
                    },
                    new FilePickerFileType("All Files") { Patterns = ["*.*"] },
                ],
            }
        );

        var outputPath = file?.TryGetLocalPath();
        if (outputPath == null)
            return;

        using var data = SKImage.FromBitmap(img).Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(outputPath, data.ToArray());

        AppendLog($"Saved current preview to {outputPath}");
    }

    private void MenuToolsOpenColourToHSVStepsTool_Click(object? sender, RoutedEventArgs e) =>
        new ColourToHSVStepsTool().Show(this);

#if DEBUG
    private void MenuDebugConnectVirtualGamepad_Click(object? sender, RoutedEventArgs e)
    {
        if (
            MenuDebugConnectVirtualGamepad == null
            || MenuDebugRunInVirtualGamepad == null
            || MenuDebugOpenVirtualGamepadController == null
        )
            return;

        if (!_debugVirtualGamepad.CheckDriver())
        {
            _ = ShowMessageAsync(
                "ViGEmBus driver not found",
                "To use this feature, you must install the ViGEmBus driver.",
                new Uri("https://github.com/nefarius/ViGEmBus/releases"),
                "Download it here"
            );
            return;
        }

        if (!_debugVirtualGamepad.IsConnected)
        {
            _debugVirtualGamepad.Connect();
            MenuDebugConnectVirtualGamepad.Header = "Disconnect Virtual Gamepad";
        }
        else
        {
            MenuDebugConnectVirtualGamepad.Header = "Re-connect Virtual Gamepad";
            _debugVirtualGamepad.Disconnect();
        }

        MenuDebugRunInVirtualGamepad.IsEnabled = _debugVirtualGamepad.IsConnected;
        MenuDebugOpenVirtualGamepadController.IsEnabled = _debugVirtualGamepad.IsConnected;
    }

    private async void MenuDebugRunInVirtualGamepad_Click(object? sender, RoutedEventArgs e)
    {
        if (!_debugVirtualGamepad.IsConnected)
            return;

        if (string.IsNullOrEmpty(_currentImagePath))
        {
            _ = ShowMessageAsync("No image selected", "Select an image first.");
            return;
        }

        var imageSnapshot = _currentImage!.Copy();
        var drawSettings = GetDrawImageSettings();
        var token = BeginGeneration();

        AppendLog(
            "Starting to draw with the Virtual Gamepad. Keep focus on the window you want to draw on for the duration of the drawing."
        );

        await Task.Run(async () =>
        {
            using var img = imageSnapshot;
            var drawer = new CanvasDrawer(
                new VirtualGamepadSink(_debugVirtualGamepad),
                _currentSettings.SelectedSwitchVersion,
                AppendLog
            );
            await drawer.DrawImage(img, drawSettings);
        });

        AppendLog("Virtual Gamepad is not longer being controller by the drawer.");
    }

    private void MenuDebugOpenVirtualGamepadController_Click(object? sender, RoutedEventArgs e)
    {
        if (!_debugVirtualGamepad.IsConnected)
            return;

        var window = new VirtualGamepadControllerWindow { VirtualGamepad = _debugVirtualGamepad };
        window.Show(this);
    }
#endif

    // Help menu -> this V2 project's repo.
    private void MenuHelpOpenGitHub_Click(object? sender, RoutedEventArgs e) =>
        Launcher.LaunchUriAsync(new Uri("https://github.com/xoaninc/TomodachiDrawer-V2"));

    // Footer credit link to the ORIGINAL project (this is a derivative; GPL-3.0).
    private void OpenOriginalRepoButton_Click(object? sender, RoutedEventArgs e) =>
        Launcher.LaunchUriAsync(new Uri("https://github.com/Lucas7yoshi/TomodachiDrawer"));

    private void MenuHelpAbout_Click(object? sender, RoutedEventArgs e)
    {
        var message = $"TomodachiDrawer V2 {GetVersionString(false)}";
        var commit = GetVersionString(true).Split("+").Last();
        message += $"\nBuilt from commit: {commit}";

        message +=
            $"\n\nOriginal TomodachiDrawer created by Lucas7yoshi and contributors."
            + $"\nV2 (ESP32-S3 support and fixes) by xoaninc."
            + $"\nThis project is Free and Open Source Software licensed under the GPLv3.0 License."
            + $"\nSource code is available on GitHub: github.com/xoaninc/TomodachiDrawer-V2"
            + $"\n\nThis program is in no way affiliated, endorsed, sponsored or created by Nintendo.";
        _ = ShowMessageAsync("About TomodachiDrawer V2", message);
    }

    private void MenuExit_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MenuHelpOpenWelcome_Click(object? sender, RoutedEventArgs e) =>
        ShowWelcomeMessage();

    private void MenuHelpCheckForUpdate_Click(object? sender, RoutedEventArgs e) =>
        _ = PerformAsyncUpdateCheck();

    private void EnableHomeCanvas_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        // TODO: Notify if non 256x256 image.
        DrawingOptionChanged();
    }

    private void ReverseColourOrderCheckBox_IsCheckedChanged(object? sender, RoutedEventArgs e)
    {
        DrawingOptionChanged();
        if (_currentImage != null)
            UpdatePreview();
    }
}
