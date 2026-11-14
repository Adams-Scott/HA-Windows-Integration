using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PcControl.Agent.Config;

/// <summary>Loads, creates and watches config.json. This is the only file the agent watches.</summary>
public sealed class ConfigStore : IDisposable
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string _path;
    private ILogger? _log;
    private FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private volatile AgentConfig _current = new();

    public ConfigStore(string path) => _path = path;

    public string FilePath => _path;
    public AgentConfig Current => _current;

    /// <summary>Raised after a successful reload: (old, new).</summary>
    public event Action<AgentConfig, AgentConfig>? Changed;

    public void AttachLogger(ILogger log) => _log = log;

    /// <summary>Loads the config, creating it with defaults on first run. Returns true on first run.</summary>
    public bool LoadOrCreate()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (!File.Exists(_path))
        {
            var cfg = new AgentConfig { ApiToken = GenerateToken() };
            cfg.Normalize();
            Save(cfg);
            _current = cfg;
            return true;
        }

        var loaded = Read() ?? new AgentConfig();
        loaded.Normalize();
        if (string.IsNullOrWhiteSpace(loaded.ApiToken))
        {
            loaded.ApiToken = GenerateToken();
            Save(loaded);
        }
        _current = loaded;
        return false;
    }

    public void StartWatching()
    {
        _debounce = new System.Threading.Timer(_ => Reload(), null, Timeout.Infinite, Timeout.Infinite);
        _watcher = new FileSystemWatcher(Path.GetDirectoryName(_path)!, Path.GetFileName(_path))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        FileSystemEventHandler h = (_, _) => _debounce.Change(500, Timeout.Infinite);
        _watcher.Changed += h;
        _watcher.Created += h;
        _watcher.Renamed += (_, _) => _debounce.Change(500, Timeout.Infinite);
    }

    private void Reload()
    {
        var next = Read();
        if (next is null) return;
        next.Normalize();
        var old = _current;
        if (string.IsNullOrWhiteSpace(next.ApiToken)) next.ApiToken = old.ApiToken;

        if (JsonSerializer.Serialize(old, JsonOptions) == JsonSerializer.Serialize(next, JsonOptions)) return;
        _current = next;
        _log?.LogInformation("Settings reloaded from {Path}", _path);
        try { Changed?.Invoke(old, next); }
        catch (Exception ex) { _log?.LogError(ex, "Error applying reloaded settings"); }
    }

    private AgentConfig? Read()
    {
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<AgentConfig>(fs, JsonOptions);
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (JsonException ex)
            {
                _log?.LogError("config.json is not valid JSON, keeping previous settings: {Message}", ex.Message);
                return null;
            }
        }
        _log?.LogError("Could not read {Path}", _path);
        return null;
    }

    private void Save(AgentConfig cfg) =>
        File.WriteAllText(_path, JsonSerializer.Serialize(cfg, JsonOptions));

    public static string GenerateToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
