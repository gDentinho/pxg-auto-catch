using System.Text.Json;

namespace PxGCorpseReader;

internal sealed class CatchRule
{
    // Legacy property kept only so old rule files deserialize cleanly.
    // v0.9 rule presence itself means "active".
    public bool Enabled { get; set; } = true;

    public string Pokemon { get; set; } = "";
    public string BallName { get; set; } = "Ultra Ball";

    // Legacy/migration snapshot. Runtime resolution uses BallCatalog.
    public int BallItemId { get; set; }

    internal bool Matches(string? creatureName)
    {
        var a = NormalizePokemonName(Pokemon);
        var b = NormalizePokemonName(creatureName);

        return a.Length > 0 &&
               string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizePokemonName(string? value)
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

internal static class CatchRuleStore
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
                "catch_rules.json");
        }
    }

    internal static IReadOnlyList<CatchRule> Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return Array.Empty<CatchRule>();

            var json = File.ReadAllText(FilePath);

            return JsonSerializer.Deserialize<List<CatchRule>>(
                    json,
                    JsonOptions)
                ?.Where(IsValid)
                .Select(x =>
                {
                    x.Enabled = true;
                    x.Pokemon =
                        CatchRule.NormalizePokemonName(x.Pokemon);
                    x.BallName =
                        BallCatalogStore.Normalize(x.BallName);

                    return x;
                })
                .ToArray()
                ?? Array.Empty<CatchRule>();
        }
        catch
        {
            return Array.Empty<CatchRule>();
        }
    }

    internal static void Save(IEnumerable<CatchRule> rules)
    {
        var dir = Path.GetDirectoryName(FilePath);

        if (!string.IsNullOrWhiteSpace(dir))
            Directory.CreateDirectory(dir);

        var cleaned = rules
            .Where(IsValid)
            .Select(x => new CatchRule
            {
                Enabled = true,
                Pokemon =
                    CatchRule.NormalizePokemonName(x.Pokemon),
                BallName =
                    BallCatalogStore.Normalize(x.BallName),
                BallItemId = Math.Clamp(x.BallItemId, 0, 65535)
            })
            .OrderBy(
                x => x.Pokemon,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

        File.WriteAllText(
            FilePath,
            JsonSerializer.Serialize(cleaned, JsonOptions));
    }

    private static bool IsValid(CatchRule rule)
        => CatchRule.NormalizePokemonName(rule.Pokemon).Length > 0 &&
           BallCatalogStore.Normalize(rule.BallName).Length > 0;
}
