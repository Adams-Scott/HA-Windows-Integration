using PcControl.Agent.Checks;

namespace PcControl.Agent.Entities;

public static class EntityStates
{
    public const string On = "on";
    public const string Off = "off";
    public const string Unknown = "unknown";
    public const string Assumed = "assumed";
}

/// <summary>A script or game the agent exposes. State is cached and refreshed by the state loop.</summary>
public abstract class Entity
{
    private int _busy;
    private volatile string _state = EntityStates.Unknown;

    protected Entity(string id, string name, string icon)
    {
        Id = id;
        Name = name;
        Icon = icon;
    }

    public string Id { get; }
    public abstract string Kind { get; }
    public string Name { get; protected set; }
    public string Icon { get; protected set; }

    public string State
    {
        get => _state;
        set => _state = value;
    }

    public bool Busy => Volatile.Read(ref _busy) == 1;

    /// <summary>Claims the entity for one action. Returns false if an action is already running.</summary>
    public bool TryBeginAction() => Interlocked.CompareExchange(ref _busy, 1, 0) == 0;

    public void EndAction() => Volatile.Write(ref _busy, 0);

    /// <summary>True if the other entity describes the same thing with identical metadata.</summary>
    public abstract bool SameMetadata(Entity other);

    /// <summary>Copies metadata (not state or busy) from a freshly scanned entity with the same ID.</summary>
    public abstract void UpdateFrom(Entity fresh);
}

public sealed class ScriptEntity : Entity
{
    public ScriptEntity(string id, string name, string icon, string path, CheckSpec? check)
        : base(id, name, icon)
    {
        Path = path;
        Check = check;
        if (check is null) State = EntityStates.Assumed;
    }

    public override string Kind => "script";
    public string Path { get; private set; }
    public CheckSpec? Check { get; private set; }

    public override bool SameMetadata(Entity other) =>
        other is ScriptEntity s && s.Name == Name && s.Icon == Icon &&
        string.Equals(s.Path, Path, StringComparison.OrdinalIgnoreCase) && s.Check?.Raw == Check?.Raw;

    public override void UpdateFrom(Entity fresh)
    {
        var s = (ScriptEntity)fresh;
        Name = s.Name;
        Icon = s.Icon;
        Path = s.Path;
        Check = s.Check;
        if (Check is null) State = EntityStates.Assumed;
        else if (State == EntityStates.Assumed) State = EntityStates.Unknown;
    }
}

public sealed class GameEntity : Entity
{
    public GameEntity(uint appId, string name, string installPath)
        : base($"steam.{appId}", name, "mdi:steam")
    {
        AppId = appId;
        InstallPath = installPath;
        State = EntityStates.Off;
    }

    public override string Kind => "game";
    public uint AppId { get; }
    public string InstallPath { get; private set; }

    /// <summary>Install path with a trailing separator, for prefix matching of process paths.</summary>
    public string InstallPrefix => InstallPath.EndsWith('\\') ? InstallPath : InstallPath + "\\";

    public override bool SameMetadata(Entity other) =>
        other is GameEntity g && g.Name == Name && string.Equals(g.InstallPath, InstallPath, StringComparison.OrdinalIgnoreCase);

    public override void UpdateFrom(Entity fresh)
    {
        var g = (GameEntity)fresh;
        Name = g.Name;
        InstallPath = g.InstallPath;
    }
}
