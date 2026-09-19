using System.Text.Json;

namespace PxGCorpseReader;

internal sealed class UserSettings
{
    public bool AutoCatchEnabled { get; set; }
    public int CorpseBallDelayMs { get; set; }
}

internal static class UserSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    internal static string FilePath
    {
        get
        {
            var baseDir = Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData);

            return Path.Combine(
                baseDir,
                "PxGCorpseReader",
                "settings.json");
        }
    }

    internal static UserSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return new UserSettings();

            var settings =
                JsonSerializer.Deserialize<UserSettings>(
                    File.ReadAllText(FilePath),
                    JsonOptions)
                ?? new UserSettings();

            settings.CorpseBallDelayMs =
                Math.Clamp(settings.CorpseBallDelayMs, 0, 5000);

            return settings;
        }
        catch
        {
            return new UserSettings();
        }
    }

    internal static void Save(UserSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        settings.CorpseBallDelayMs =
            Math.Clamp(settings.CorpseBallDelayMs, 0, 5000);

        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(settings, JsonOptions));
    }
}
