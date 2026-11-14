namespace PcControl.Agent.Checks;

public enum CheckType
{
    Process,
    Service,
    Port,
    Window,
    PowerShell,
    Invalid,
}

/// <summary>A parsed HA-Check header value, for example <c>process:Plex Media Server.exe</c>.</summary>
public sealed class CheckSpec
{
    private CheckSpec(CheckType type, string raw, string argument, IReadOnlyList<string> names, int port)
    {
        Type = type;
        Raw = raw;
        Argument = argument;
        ProcessNames = names;
        Port = port;
    }

    public CheckType Type { get; }
    public string Raw { get; }
    public string Argument { get; }

    /// <summary>Process names without ".exe" (process: checks only).</summary>
    public IReadOnlyList<string> ProcessNames { get; }

    public int Port { get; }

    public string? Error { get; private init; }

    public static CheckSpec Parse(string raw)
    {
        raw = raw.Trim();
        int colon = raw.IndexOf(':');
        if (colon <= 0) return Invalid(raw, "expected <type>:<value>");

        string prefix = raw[..colon].Trim().ToLowerInvariant();
        string arg = raw[(colon + 1)..].Trim();
        if (arg.Length == 0) return Invalid(raw, "empty value");

        switch (prefix)
        {
            case "process":
                var names = arg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(StripExe)
                    .Where(n => n.Length > 0)
                    .ToArray();
                return names.Length == 0
                    ? Invalid(raw, "no process names")
                    : new CheckSpec(CheckType.Process, raw, arg, names, 0);
            case "service":
                return new CheckSpec(CheckType.Service, raw, arg, [], 0);
            case "port":
                return int.TryParse(arg, out int port) && port is > 0 and <= 65535
                    ? new CheckSpec(CheckType.Port, raw, arg, [], port)
                    : Invalid(raw, "port must be 1-65535");
            case "window":
                return new CheckSpec(CheckType.Window, raw, arg, [], 0);
            case "ps":
                return new CheckSpec(CheckType.PowerShell, raw, arg, [], 0);
            default:
                return Invalid(raw, $"unknown check type '{prefix}'");
        }
    }

    public static string StripExe(string name)
    {
        name = name.Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4].TrimEnd() : name;
    }

    private static CheckSpec Invalid(string raw, string error) =>
        new(CheckType.Invalid, raw, "", [], 0) { Error = error };
}
