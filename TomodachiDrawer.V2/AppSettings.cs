using System.Text.Json;
using System.Text.Json.Serialization;
using TomodachiDrawer.Core.Models;

namespace TomodachiDrawer.UI.Avalonia;

internal class AppSettings
{
    public SwitchVersion SelectedSwitchVersion { get; set; } = SwitchVersion.None;

    public int SelectedThemeIndex { get; set; } = 0;

    public bool EnableExperimentalFeatures { get; set; } = false;

    public bool CheckForUpdatesOnStart { get; set; } = true;

    public int FirstStartId { get; set; } = 0;

    // ===== Drawing options, remembered between sessions =====
    // These used to reset on every launch, which is tedious if you always use the same colour
    // matcher or always draw at a particular TSP time limit.

    public string ColourMatcherName { get; set; } = "Arbitrary";

    public int ColourLimit { get; set; } = 16;

    /// <summary>"None", or a key from <c>ImageDenoiser.Denoisers</c>.</summary>
    public string DenoiserName { get; set; } = "None";

    public bool HomeToTopLeft { get; set; } = false;

    public bool ReverseColourOrder { get; set; } = false;

    // TSP tuning knobs. Off by default — upstream ships them disabled because the two
    // coefficients are unintuitive to tune.
    public bool EarlyTspExitEnabled { get; set; } = false;

    public double EarlyTspExitRateCoefficient { get; set; } = 0.05;

    public int EarlyTspExitSolutionsDistance { get; set; } = 10;

    /// <summary>
    /// Where <c>settings.json</c> lives: always under the per-user application-data folder, never
    /// next to the executable. The install directory is frequently read-only (Program Files, a
    /// macOS <c>.app</c> bundle, a read-only mount), and the process working directory is whatever
    /// the launcher happened to set — for an <c>.app</c> opened from Finder that is <c>/</c>.
    /// </summary>
    public static string FilePath
    {
        get
        {
            var folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "TomodachiDrawerV2"
            );
            Directory.CreateDirectory(folder); // no-op when it already exists
            return Path.Combine(folder, "settings.json");
        }
    }

    /// <summary>Legacy location — the working directory. Read once so existing installs keep their
    /// preferences on the first run of a build that stores them in application data.</summary>
    private const string LegacyFileName = "settings.json";

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, AppSettingsContext.Default.AppSettings);
            File.WriteAllText(FilePath, json);
        }
        catch (Exception)
        {
            // Losing preferences is not worth taking the app down for.
        }
    }

    /// <summary>
    /// Loads settings, migrating the legacy working-directory file forward if that is all there is.
    /// Never throws — a corrupt or unreadable file falls back to defaults and reports via
    /// <paramref name="warning"/>.
    /// </summary>
    public static AppSettings Load(out string? warning)
    {
        warning = null;

        foreach (var path in new[] { FilePath, LegacyFileName })
        {
            if (!File.Exists(path))
                continue;
            try
            {
                var json = File.ReadAllText(path);
                var loaded = JsonSerializer.Deserialize(
                    json,
                    AppSettingsContext.Default.AppSettings
                );
                if (loaded == null)
                    continue;

                if (path == LegacyFileName)
                {
                    // Migrate forward so the next save does not land in the working directory.
                    loaded.Save();
                    try
                    {
                        File.Delete(LegacyFileName);
                    }
                    catch (Exception)
                    {
                        // Best effort — the new copy is what gets read from now on.
                    }
                }
                return loaded;
            }
            catch (Exception)
            {
                warning = $"Failed to load settings from {path}. Using defaults.";
            }
        }

        return new AppSettings();
    }
}

// Source gen serialization to avoid trimming warnings.
[JsonSerializable(typeof(AppSettings))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class AppSettingsContext : JsonSerializerContext { }
