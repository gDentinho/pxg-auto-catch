using System.Text.Json;

namespace PxGCorpseReader;

internal sealed class BallCatalogEntry
{
    public string Name { get; set; } = "";
    public int ItemId { get; set; }

    public override string ToString() => Name;
}

internal static class BallCatalogStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    // Names observed in the PxG/MacroHelpers catch UI.
    // Only IDs actually validated by this project are pre-filled.
    private static readonly (string Name, int ItemId)[] Defaults =
    {
        ("Dusk Ball", 0),
        ("Fast Ball", 0),
        ("Great Ball", 0),
        ("Heavy Ball", 0),
        ("Janguru Ball", 0),
        ("Magu Ball", 0),
        ("Moon Ball", 0),
        ("Net Ball", 0),
        ("Poke Ball", 0),
        ("Premier Ball", 0),
        ("Sora Ball", 0),
        ("Super Ball", 0),
        ("Tale Ball", 0),
        ("Tinker Ball", 0),
        ("Ultra Ball", 2652),
        ("Ultra Safari Balls", 0),
        ("Yume Ball", 0)
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
                "ball_catalog.json");
        }
    }

    internal static IReadOnlyList<BallCatalogEntry> Load(
        IEnumerable<CatchRule>? legacyRules = null)
    {
        var result = Defaults
            .Select(x => new BallCatalogEntry
            {
                Name = x.Name,
                ItemId = x.ItemId
            })
            .ToDictionary(
                x => Normalize(x.Name),
                x => x,
                StringComparer.OrdinalIgnoreCase);

        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var saved =
                    JsonSerializer.Deserialize<List<BallCatalogEntry>>(
                        json,
                        JsonOptions)
                    ?? new();

                foreach (var entry in saved)
                {
                    var key = Normalize(entry.Name);

                    if (key.Length == 0)
                        continue;

                    result[key] = new BallCatalogEntry
                    {
                        Name = entry.Name.Trim(),
                        ItemId = Math.Clamp(entry.ItemId, 0, 65535)
                    };
                }
            }
        }
        catch
        {
        }

        // Migration: v0.7/v0.8 stored the Item ID inside each Pokémon rule.
        // Preserve mappings the user already validated there.
        if (legacyRules is not null)
        {
            foreach (var rule in legacyRules)
            {
                var key = Normalize(rule.BallName);

                if (key.Length == 0 ||
                    rule.BallItemId <= 0 ||
                    rule.BallItemId > 65535)
                {
                    continue;
                }

                if (!result.TryGetValue(key, out var existing) ||
                    existing.ItemId == 0)
                {
                    result[key] = new BallCatalogEntry
                    {
                        Name = rule.BallName.Trim(),
                        ItemId = rule.BallItemId
                    };
                }
            }
        }

        return result.Values
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static void Save(IEnumerable<BallCatalogEntry> entries)
    {
        var dir = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var cleaned = entries
            .Select(x => new BallCatalogEntry
            {
                Name = Normalize(x.Name),
                ItemId = Math.Clamp(x.ItemId, 0, 65535)
            })
            .Where(x => x.Name.Length > 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Last())
            .OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(cleaned, JsonOptions));
    }

    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        return string.Join(
            " ",
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries));
    }
}
