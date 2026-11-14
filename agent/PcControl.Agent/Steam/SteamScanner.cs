using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using PcControl.Agent.Config;
using PcControl.Agent.Entities;

namespace PcControl.Agent.Steam;

/// <summary>Finds installed Steam games from libraryfolders.vdf and appmanifest_*.acf.</summary>
public sealed class SteamScanner
{
    public const uint SteamworksRedistAppId = 228980;
    private readonly ILogger _log;

    public SteamScanner(ILogger log) => _log = log;

    public static string? GetSteamPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        if (key?.GetValue("SteamPath") is not string raw || string.IsNullOrWhiteSpace(raw)) return null;
        try { return Path.GetFullPath(raw.Replace('/', '\\')); }
        catch { return null; }
    }

    public List<GameEntity> Scan(SteamConfig cfg)
    {
        var games = new List<GameEntity>();
        if (!cfg.Enabled) return games;

        var steamPath = GetSteamPath();
        if (steamPath is null || !Directory.Exists(steamPath))
        {
            _log.LogInformation("Steam not found (no SteamPath in HKCU\\Software\\Valve\\Steam); Steam games disabled");
            return games;
        }

        var seen = new HashSet<uint>();
        foreach (var library in ReadLibraryFolders(steamPath, _log))
        {
            var steamapps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(steamapps)) continue;

            IEnumerable<string> manifests;
            try { manifests = Directory.EnumerateFiles(steamapps, "appmanifest_*.acf").ToList(); }
            catch (Exception ex)
            {
                _log.LogWarning("Could not list {Folder}: {Message}", steamapps, ex.Message);
                continue;
            }

            foreach (var manifest in manifests)
            {
                try
                {
                    var game = ParseManifest(library, File.ReadAllText(manifest));
                    if (game is null || !seen.Add(game.AppId)) continue;
                    if (IsExcluded(game, cfg)) continue;
                    games.Add(game);
                }
                catch (Exception ex)
                {
                    _log.LogWarning("Could not parse {Manifest}: {Message}", manifest, ex.Message);
                }
            }
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return games;
    }

    public static bool IsExcluded(GameEntity game, SteamConfig cfg) =>
        game.AppId == SteamworksRedistAppId ||
        cfg.ExcludeAppIds.Contains(game.AppId) ||
        cfg.ExcludeNamePatterns.Any(p => !string.IsNullOrWhiteSpace(p) &&
                                         game.Name.Contains(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Library roots from libraryfolders.vdf, always including the Steam folder itself.</summary>
    public static List<string> ReadLibraryFolders(string steamPath, ILogger? log = null)
    {
        var result = new List<string> { steamPath };
        var vdfPath = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdfPath)) return result;

        try
        {
            var root = VdfParser.Parse(File.ReadAllText(vdfPath));
            var folders = root.Obj("libraryfolders") ?? root.Obj("LibraryFolders") ?? root;
            foreach (var (key, value) in folders)
            {
                if (!key.All(char.IsDigit)) continue;
                // New format: "0" { "path" "..." }. Old format: "1" "D:\\SteamLibrary".
                string? path = value switch
                {
                    Dictionary<string, object> d => d.Str("path"),
                    string s => s,
                    _ => null,
                };
                if (string.IsNullOrWhiteSpace(path)) continue;
                path = Path.GetFullPath(path.Replace('/', '\\'));
                if (!result.Any(r => string.Equals(r.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                    result.Add(path);
            }
        }
        catch (Exception ex)
        {
            log?.LogWarning("Could not parse {File}: {Message}", vdfPath, ex.Message);
        }
        return result;
    }

    public static GameEntity? ParseManifest(string libraryPath, string acfText)
    {
        var root = VdfParser.Parse(acfText);
        var app = root.Obj("AppState");
        if (app is null) return null;
        if (!uint.TryParse(app.Str("appid"), out uint appId)) return null;
        var name = app.Str("name");
        var installDir = app.Str("installdir");
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(installDir)) return null;
        var installPath = Path.Combine(libraryPath, "steamapps", "common", installDir);
        return new GameEntity(appId, name.Trim(), installPath);
    }
}
