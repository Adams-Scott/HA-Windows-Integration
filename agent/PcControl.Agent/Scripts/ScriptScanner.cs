using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Checks;
using PcControl.Agent.Entities;

namespace PcControl.Agent.Scripts;

/// <summary>Reads the HA-* header keys from the first 30 lines of a script.</summary>
public static partial class ScriptHeaderParser
{
    public const int MaxHeaderLines = 30;

    [GeneratedRegex(@"^\s*#\s*HA-(\w+)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LineComment();

    // Inside a <# ... #> block the leading '#' is optional.
    [GeneratedRegex(@"^\s*#?\s*HA-(\w+)\s*:\s*(.+?)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockLine();

    public static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        bool inBlock = false;

        foreach (var raw in lines.Take(MaxHeaderLines))
        {
            string line = raw;
            if (!inBlock)
            {
                int open = line.IndexOf("<#", StringComparison.Ordinal);
                if (open >= 0)
                {
                    inBlock = true;
                    line = line[(open + 2)..];
                }
            }

            if (inBlock)
            {
                int close = line.IndexOf("#>", StringComparison.Ordinal);
                string inner = close >= 0 ? line[..close] : line;
                if (close >= 0) inBlock = false;
                Add(headers, BlockLine().Match(inner));
            }
            else
            {
                Add(headers, LineComment().Match(line));
            }
        }
        return headers;
    }

    private static void Add(Dictionary<string, string> headers, Match m)
    {
        if (m.Success) headers.TryAdd(m.Groups[1].Value, m.Groups[2].Value);
    }
}

public static partial class Slug
{
    [GeneratedRegex("[^a-z0-9]")]
    private static partial Regex NonAlnum();

    /// <summary>"Plex Server.ps1" -> "plex_server".</summary>
    public static string FromFileName(string fileName)
    {
        var name = fileName.EndsWith(".ps1", StringComparison.OrdinalIgnoreCase) ? fileName[..^4] : fileName;
        return NonAlnum().Replace(name.ToLowerInvariant(), "_");
    }
}

/// <summary>Scans the top level of the scripts folder for *.ps1 files.</summary>
public sealed class ScriptScanner
{
    public const string DefaultIcon = "mdi:application";
    private readonly ILogger _log;

    public ScriptScanner(ILogger log) => _log = log;

    public List<ScriptEntity> Scan(string folder)
    {
        var result = new List<ScriptEntity>();
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            _log.LogError("Could not create scripts folder {Folder}: {Message}", folder, ex.Message);
            return result;
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        // Note: "*.ps1" would also match "*.ps1xml" on Windows, so filter the extension explicitly.
        foreach (var path in Directory.EnumerateFiles(folder, "*.ps1", SearchOption.TopDirectoryOnly)
                     .Where(p => Path.GetExtension(p).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var entity = FromFile(path);
                if (entity is null) continue;
                if (!ids.Add(entity.Id))
                {
                    _log.LogWarning("Skipping {Path}: entity ID {Id} is already used by another script", path, entity.Id);
                    continue;
                }
                result.Add(entity);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Could not read script {Path}: {Message}", path, ex.Message);
            }
        }
        return result;
    }

    public ScriptEntity? FromFile(string path)
    {
        var headers = ScriptHeaderParser.Parse(File.ReadLines(path).Take(ScriptHeaderParser.MaxHeaderLines).ToList());
        return FromHeaders(path, headers, _log);
    }

    public static ScriptEntity? FromHeaders(string path, IReadOnlyDictionary<string, string> headers, ILogger? log = null)
    {
        if (headers.TryGetValue("Disabled", out var disabled) &&
            disabled.Trim().ToLowerInvariant() is "true" or "yes" or "1")
        {
            log?.LogInformation("Skipping disabled script {Path}", path);
            return null;
        }

        var fileName = Path.GetFileName(path);
        var name = headers.TryGetValue("Name", out var n) && n.Length > 0 ? n : Path.GetFileNameWithoutExtension(path);
        var icon = headers.TryGetValue("Icon", out var i) && i.Length > 0 ? i : DefaultIcon;

        CheckSpec? check = null;
        if (headers.TryGetValue("Check", out var c))
        {
            check = CheckSpec.Parse(c);
            if (check.Type == CheckType.Invalid)
                log?.LogWarning("Script {Path} has an invalid HA-Check '{Check}': {Error}. Its state will be unknown",
                    path, c, check.Error);
        }

        return new ScriptEntity("script." + Slug.FromFileName(fileName), name, icon, path, check);
    }
}
