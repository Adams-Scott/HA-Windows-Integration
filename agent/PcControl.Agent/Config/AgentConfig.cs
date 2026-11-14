using System.Text.Json.Serialization;

namespace PcControl.Agent.Config;

/// <summary>Contents of %APPDATA%\PcControl\config.json.</summary>
public sealed class AgentConfig
{
    public int Port { get; set; } = 8765;
    public string BindAddress { get; set; } = "0.0.0.0";
    public string ApiToken { get; set; } = "";
    public string ScriptsFolder { get; set; } = @"%USERPROFILE%\Documents\PcControl\Scripts";
    public int ScriptTimeoutSeconds { get; set; } = 60;
    public int StateIntervalSeconds { get; set; } = 5;
    public SteamConfig Steam { get; set; } = new();
    public int ShutdownDelaySeconds { get; set; } = 5;

    [JsonIgnore]
    public string ScriptsFolderExpanded => Environment.ExpandEnvironmentVariables(ScriptsFolder ?? "");

    /// <summary>Fixes nulls and out-of-range values after deserialisation.</summary>
    public void Normalize()
    {
        if (Port is < 1 or > 65535) Port = 8765;
        if (string.IsNullOrWhiteSpace(BindAddress)) BindAddress = "0.0.0.0";
        ApiToken ??= "";
        if (string.IsNullOrWhiteSpace(ScriptsFolder)) ScriptsFolder = new AgentConfig().ScriptsFolder;
        ScriptTimeoutSeconds = Math.Clamp(ScriptTimeoutSeconds, 1, 3600);
        StateIntervalSeconds = Math.Clamp(StateIntervalSeconds, 1, 300);
        ShutdownDelaySeconds = Math.Clamp(ShutdownDelaySeconds, 0, 600);
        Steam ??= new SteamConfig();
        Steam.ExcludeAppIds ??= [];
        Steam.ExcludeNamePatterns ??= [];
    }
}

public sealed class SteamConfig
{
    public bool Enabled { get; set; } = true;
    public List<uint> ExcludeAppIds { get; set; } = [];
    public List<string> ExcludeNamePatterns { get; set; } =
        ["Redistributable", "Runtime", "SDK", "Dedicated Server", "Soundtrack"];
}
