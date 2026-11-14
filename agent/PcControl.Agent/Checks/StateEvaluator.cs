using System.ServiceProcess;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Entities;
using PcControl.Agent.Steam;

namespace PcControl.Agent.Checks;

/// <summary>Works out an entity's state from a snapshot.</summary>
public sealed class StateEvaluator
{
    private static readonly TimeSpan PsTimeout = TimeSpan.FromSeconds(5);
    private readonly PowerShellHost _ps;
    private readonly ILogger _log;

    public StateEvaluator(PowerShellHost ps, ILogger log)
    {
        _ps = ps;
        _log = log;
    }

    public string Evaluate(Entity entity, StateSnapshot snap)
    {
        try
        {
            return entity switch
            {
                ScriptEntity s => EvaluateScript(s, snap),
                GameEntity g => SteamGames.IsRunning(g, snap) ? EntityStates.On : EntityStates.Off,
                _ => EntityStates.Unknown,
            };
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "State check failed for {EntityId}", entity.Id);
            return EntityStates.Unknown;
        }
    }

    private string EvaluateScript(ScriptEntity s, StateSnapshot snap)
    {
        var check = s.Check;
        if (check is null) return EntityStates.Assumed;

        bool? on = check.Type switch
        {
            CheckType.Process => check.ProcessNames.Any(snap.HasProcess),
            CheckType.Service => ServiceRunning(snap, check.Argument),
            CheckType.Port => snap.IsListening(check.Port),
            CheckType.Window => snap.WindowTitles.Any(t => t.Contains(check.Argument, StringComparison.OrdinalIgnoreCase)),
            CheckType.PowerShell => _ps.Evaluate(check.Argument, PsTimeout),
            _ => null,
        };

        return on switch
        {
            true => EntityStates.On,
            false => EntityStates.Off,
            null => EntityStates.Unknown,
        };
    }

    private static bool? ServiceRunning(StateSnapshot snap, string name)
    {
        var svc = snap.Services.FirstOrDefault(s =>
            s.ServiceName.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            s.DisplayName.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (svc is null) return null;
        return svc.Status == ServiceControllerStatus.Running;
    }
}
