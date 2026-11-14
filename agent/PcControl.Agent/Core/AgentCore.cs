using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Config;
using PcControl.Agent.Entities;
using PcControl.Agent.Scripts;
using PcControl.Agent.Steam;

namespace PcControl.Agent.Core;

/// <summary>Ties scanning, state and actions together. Shared by the tray and the API.</summary>
public sealed class AgentCore
{
    private readonly object _scanLock = new();
    private readonly ScriptScanner _scripts;
    private readonly SteamScanner _steam;
    private readonly ILogger _log;

    public AgentCore(ConfigStore config, EntityStore store, StateLoop stateLoop, ActionRunner actions,
        ScriptScanner scripts, SteamScanner steam, ILogger log)
    {
        Config = config;
        Store = store;
        StateLoop = stateLoop;
        Actions = actions;
        _scripts = scripts;
        _steam = steam;
        _log = log;
    }

    public static string Version { get; } =
        typeof(AgentCore).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "1.0.0";

    public ConfigStore Config { get; }
    public EntityStore Store { get; }
    public StateLoop StateLoop { get; }
    public ActionRunner Actions { get; }

    /// <summary>Raised after every rescan with (scripts, games).</summary>
    public event Action<int, int>? Rescanned;

    public (int Scripts, int Games) Rescan()
    {
        lock (_scanLock)
        {
            var cfg = Config.Current;
            var scripts = _scripts.Scan(cfg.ScriptsFolderExpanded);
            List<GameEntity> games;
            try { games = _steam.Scan(cfg.Steam); }
            catch (Exception ex)
            {
                _log.LogError(ex, "Steam scan failed");
                games = [];
            }

            bool changed = Store.Replace(scripts.Cast<Entity>().Concat(games));
            StateLoop.Tick();
            _log.LogInformation("Rescan: {Scripts} scripts, {Games} games (revision {Revision}{Changed})",
                scripts.Count, games.Count, Store.Revision, changed ? ", changed" : "");
            Rescanned?.Invoke(scripts.Count, games.Count);
            return (scripts.Count, games.Count);
        }
    }

    public int Shutdown()
    {
        int delay = Config.Current.ShutdownDelaySeconds;
        var psi = new ProcessStartInfo("shutdown.exe") { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("/s");
        psi.ArgumentList.Add("/t");
        psi.ArgumentList.Add(delay.ToString());
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("shutdown.exe did not start");
        _log.LogInformation("Shutdown requested, delay {Delay}s", delay);
        return delay;
    }
}
