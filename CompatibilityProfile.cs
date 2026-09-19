using System.Text.Json;

namespace PxGCorpseReader;

internal sealed class CompatibilityProfile
{
    public int Schema { get; set; } = 1;
    public string PxgSha256 { get; set; } = "";
    public string Source { get; set; } = "";
    public ulong GameRva { get; set; }
    public ulong MapPointerRva { get; set; }
    public ulong LuaInterfaceSlotRva { get; set; }
    public ulong LuaPCallRva { get; set; }
    public ulong LuaLoadBufferXRva { get; set; }
    public bool Validated { get; set; }
    public DateTime ValidatedAtUtc { get; set; }

    internal CompatibilityProfile Clone(string source)
        => new()
        {
            Schema = Schema,
            PxgSha256 = PxgSha256,
            Source = source,
            GameRva = GameRva,
            MapPointerRva = MapPointerRva,
            LuaInterfaceSlotRva = LuaInterfaceSlotRva,
            LuaPCallRva = LuaPCallRva,
            LuaLoadBufferXRva = LuaLoadBufferXRva,
            Validated = Validated,
            ValidatedAtUtc = ValidatedAtUtc
        };

    internal static CompatibilityProfile KnownCurrent(string sha256)
        => new()
        {
            PxgSha256 = sha256,
            Source = "known-profile",
            GameRva = 0x01334020,
            MapPointerRva = 0x01104740,
            LuaInterfaceSlotRva = 0x01104730,
            LuaPCallRva = 0x00A2B380,
            LuaLoadBufferXRva = 0x00A2CA10,
            Validated = true,
            ValidatedAtUtc = DateTime.UtcNow
        };
}

internal static class CompatibilityProfileStore
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
                "compatibility_profiles.json");
        }
    }

    internal static CompatibilityProfile? TryLoad(string sha256)
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var list = JsonSerializer.Deserialize<List<CompatibilityProfile>>(
                    File.ReadAllText(FilePath),
                    JsonOptions)
                ?? new();

            return list.LastOrDefault(x =>
                string.Equals(
                    x.PxgSha256,
                    sha256,
                    StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    internal static void Save(CompatibilityProfile profile)
    {
        var dir = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var list = new List<CompatibilityProfile>();

        try
        {
            if (File.Exists(FilePath))
            {
                list = JsonSerializer.Deserialize<List<CompatibilityProfile>>(
                        File.ReadAllText(FilePath),
                        JsonOptions)
                    ?? new();
            }
        }
        catch
        {
            list = new();
        }

        list.RemoveAll(x =>
            string.Equals(
                x.PxgSha256,
                profile.PxgSha256,
                StringComparison.OrdinalIgnoreCase));

        list.Add(profile);

        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(
                list.OrderByDescending(x => x.ValidatedAtUtc),
                JsonOptions));
    }
}
