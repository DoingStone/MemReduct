using System.Text.Json;
using System.Text.Json.Serialization;
using MemReduct.Core.Cleaners;

namespace MemReduct.Gui;

/// <summary>
/// User preferences, persisted as JSON under <c>%APPDATA%\MemReduct\settings.json</c>.
/// <para>
/// Every read and write is best-effort: a missing, unreadable, or corrupt settings file must
/// never stop the tool from starting, because its whole job is to be available when the machine
/// is under memory pressure.
/// </para>
/// </summary>
public sealed class AppSettings
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        // Storing CleanArea as names keeps the file readable and survives flag reordering.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Directory holding the settings file.</summary>
    public static string ConfigDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MemReduct");

    /// <summary>Full path of the settings file.</summary>
    public static string ConfigFilePath => Path.Combine(ConfigDirectory, "settings.json");

    /// <summary>Areas cleaned by the Clean button and by the automatic timer.</summary>
    public CleanArea Areas { get; set; } = CleanAreas.Default;

    /// <summary>Whether the automatic timer fires at all.</summary>
    public bool AutoCleanEnabled { get; set; }

    /// <summary>Minutes between automatic cleans.</summary>
    public int AutoCleanIntervalMinutes { get; set; } = 30;

    /// <summary>Also clean as soon as memory load crosses <see cref="AutoCleanThresholdPercent"/>.</summary>
    public bool AutoCleanOnThreshold { get; set; }

    /// <summary>Memory load percentage that triggers an immediate automatic clean.</summary>
    public int AutoCleanThresholdPercent { get; set; } = 85;

    /// <summary>Show a tray balloon after a clean that released something.</summary>
    public bool ShowNotifications { get; set; } = true;

    /// <summary>Closing the window hides it to the tray instead of exiting.</summary>
    public bool MinimizeToTray { get; set; } = true;

    /// <summary>How often the live statistics are resampled, in milliseconds.</summary>
    public int RefreshIntervalMs { get; set; } = 1000;

    /// <summary>Which palette the window is drawn in.</summary>
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    /// <summary>Type scale, 1.0 being the designed size.</summary>
    public double UiScale { get; set; } = 1.0;

    /// <summary>Whether MemReduct registers itself in the per-user Run key.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Loads the settings, falling back to defaults on any failure.</summary>
    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(ConfigFilePath);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
            return loaded is null ? new AppSettings() : Sanitize(loaded);
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    /// <summary>Saves the settings, silently ignoring failures.</summary>
    public void Save()
    {
        try
        {
            System.IO.Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigFilePath, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception)
        {
            // Preferences are a convenience; losing them is not worth interrupting the user.
        }
    }

    /// <summary>
    /// Clamps values that a hand-edited file could otherwise turn into a stalled UI
    /// (a zero refresh interval, a negative timer interval).
    /// </summary>
    private static AppSettings Sanitize(AppSettings settings)
    {
        settings.RefreshIntervalMs = Math.Clamp(settings.RefreshIntervalMs, 250, 60_000);
        settings.AutoCleanIntervalMinutes = Math.Clamp(settings.AutoCleanIntervalMinutes, 1, 1440);
        settings.AutoCleanThresholdPercent = Math.Clamp(settings.AutoCleanThresholdPercent, 10, 99);
        settings.UiScale = Math.Clamp(settings.UiScale, 0.75, 2.0);
        return settings;
    }
}
