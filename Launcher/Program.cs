using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace PxGAutoCatchLauncher;

internal sealed class LauncherConfig
{
    public bool Enabled { get; set; } = true;
    public string GitHubOwner { get; set; } = "gDentinho";
    public string GitHubRepo { get; set; } = "pxg-auto-catch";
    public string AppExe { get; set; } = "PxGCorpseReader.exe";
    public int TimeoutSeconds { get; set; } = 8;
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")]
    public string TagName { get; set; } = "";

    [JsonPropertyName("prerelease")]
    public bool Prerelease { get; set; }

    [JsonPropertyName("draft")]
    public bool Draft { get; set; }

    [JsonPropertyName("assets")]
    public List<GitHubAsset> Assets { get; set; } = new();
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("browser_download_url")]
    public string DownloadUrl { get; set; } = "";
}

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    [STAThread]
    private static async Task Main()
    {
        ApplicationConfiguration.Initialize();

        string baseDir = AppContext.BaseDirectory;
        string logPath = Path.Combine(baseDir, "launcher.log");
        var config = LoadConfig(baseDir);
        string appPath = Path.Combine(baseDir, config.AppExe);

        try
        {
            if (config.Enabled &&
                !string.IsNullOrWhiteSpace(config.GitHubOwner) &&
                !string.IsNullOrWhiteSpace(config.GitHubRepo))
            {
                await CheckAndApplyGitHubUpdateAsync(
                    baseDir,
                    appPath,
                    config,
                    logPath);
            }
        }
        catch (Exception ex)
        {
            AppendLog(logPath, $"UPDATE_CHECK_FAILED {ex.Message}");
            // Network/GitHub failures must not block the installed program.
        }

        if (!File.Exists(appPath))
        {
            MessageBox.Show(
                $"Aplicativo não encontrado:\n{appPath}",
                "PxG Auto Catch",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = appPath,
            WorkingDirectory = baseDir,
            UseShellExecute = true
        });
    }

    private static LauncherConfig LoadConfig(string baseDir)
    {
        string path = Path.Combine(baseDir, "update_config.json");

        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<LauncherConfig>(
                        File.ReadAllText(path),
                        JsonOptions)
                    ?? new LauncherConfig();
            }
        }
        catch
        {
        }

        return new LauncherConfig();
    }

    private static async Task CheckAndApplyGitHubUpdateAsync(
        string baseDir,
        string appPath,
        LauncherConfig config,
        string logPath)
    {
        using var http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(
                Math.Clamp(config.TimeoutSeconds, 3, 60))
        };

        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "PxGAutoCatchLauncher/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd(
            "application/vnd.github+json");

        string api =
            $"https://api.github.com/repos/" +
            $"{Uri.EscapeDataString(config.GitHubOwner)}/" +
            $"{Uri.EscapeDataString(config.GitHubRepo)}/releases/latest";

        string json = await http.GetStringAsync(api);

        var release = JsonSerializer.Deserialize<GitHubRelease>(
            json,
            JsonOptions)
            ?? throw new InvalidDataException("GitHub release vazia.");

        if (release.Draft || release.Prerelease)
            return;

        if (!TryVersion(release.TagName, out var remoteVersion))
            throw new InvalidDataException("Tag da latest release não é uma versão válida.");

        Version installedVersion = GetInstalledVersion(appPath);

        AppendLog(
            logPath,
            $"UPDATE_CHECK installed={installedVersion}; remote={remoteVersion}; tag={release.TagName}");

        if (remoteVersion <= installedVersion)
            return;

        var zipAsset = release.Assets
            .FirstOrDefault(x =>
                x.Name.StartsWith("PxGAutoCatch-v", StringComparison.OrdinalIgnoreCase) &&
                x.Name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase));

        if (zipAsset is null)
            throw new InvalidDataException("Latest release não possui pacote win-x64 esperado.");

        var shaAsset = release.Assets
            .FirstOrDefault(x =>
                string.Equals(
                    x.Name,
                    zipAsset.Name + ".sha256",
                    StringComparison.OrdinalIgnoreCase));

        if (shaAsset is null)
            throw new InvalidDataException("Latest release não possui arquivo .sha256 do pacote.");

        byte[] zipBytes = await http.GetByteArrayAsync(zipAsset.DownloadUrl);
        string expectedSha = (await http.GetStringAsync(shaAsset.DownloadUrl))
            .Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault()
            ?? "";

        string actualSha = Convert.ToHexString(SHA256.HashData(zipBytes));

        if (expectedSha.Length != 64 ||
            !string.Equals(
                actualSha,
                expectedSha,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"SHA-256 do pacote divergente. esperado={expectedSha}; atual={actualSha}");
        }

        string workDir = Path.Combine(
            Path.GetTempPath(),
            "PxGAutoCatchUpdate",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(workDir);

        string zipPath = Path.Combine(workDir, "update.zip");
        string stageDir = Path.Combine(workDir, "stage");

        await File.WriteAllBytesAsync(zipPath, zipBytes);
        ZipFile.ExtractToDirectory(zipPath, stageDir, overwriteFiles: true);

        string stagedApp = Directory
            .EnumerateFiles(stageDir, config.AppExe, SearchOption.AllDirectories)
            .FirstOrDefault()
            ?? throw new InvalidDataException(
                $"Pacote não contém {config.AppExe}.");

        string packageRoot = Path.GetDirectoryName(stagedApp)
            ?? throw new InvalidDataException("Root do pacote inválido.");

        foreach (string source in Directory.EnumerateFiles(
            packageRoot,
            "*",
            SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(packageRoot, source);

            // Stable bootstrapper: it never overwrites itself while running.
            if (string.Equals(
                Path.GetFileName(relative),
                "PxGAutoCatch.exe",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // User/repository updater selection remains local.
            if (string.Equals(
                relative,
                "update_config.json",
                StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string destination = Path.Combine(baseDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

            if (File.Exists(destination) &&
                FilesHaveSameSha256(source, destination))
            {
                continue;
            }

            File.Copy(source, destination, overwrite: true);
        }

        AppendLog(
            logPath,
            $"UPDATE_APPLIED version={remoteVersion}; sha256={actualSha}; asset={zipAsset.Name}");

        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch
        {
        }
    }

    private static bool FilesHaveSameSha256(string a, string b)
    {
        try
        {
            using var fa = File.OpenRead(a);
            using var fb = File.OpenRead(b);

            var ha = SHA256.HashData(fa);
            var hb = SHA256.HashData(fb);

            return ha.AsSpan().SequenceEqual(hb);
        }
        catch
        {
            return false;
        }
    }

    private static Version GetInstalledVersion(string appPath)
    {
        if (!File.Exists(appPath))
            return new Version(0, 0, 0, 0);

        try
        {
            string? version = FileVersionInfo
                .GetVersionInfo(appPath)
                .ProductVersion;

            return TryVersion(version, out var parsed)
                ? parsed
                : new Version(0, 0, 0, 0);
        }
        catch
        {
            return new Version(0, 0, 0, 0);
        }
    }

    private static bool TryVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0, 0);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        string clean = text.Trim();

        if (clean.StartsWith('v') || clean.StartsWith('V'))
            clean = clean[1..];

        int plus = clean.IndexOf('+');
        if (plus >= 0)
            clean = clean[..plus];

        int dash = clean.IndexOf('-');
        if (dash >= 0)
            clean = clean[..dash];

        if (Version.TryParse(clean, out var parsed))
        {
            version = parsed;
            return true;
        }

        return false;
    }

    private static void AppendLog(string path, string message)
    {
        try
        {
            File.AppendAllText(
                path,
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
