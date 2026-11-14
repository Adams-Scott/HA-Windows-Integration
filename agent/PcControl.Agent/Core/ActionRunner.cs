using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Config;
using PcControl.Agent.Entities;
using PcControl.Agent.Steam;

namespace PcControl.Agent.Core;

public enum ActionOutcome
{
    Started,
    Partial,
    NotFound,
    Busy,
    Failed,
}

public sealed record ActionResult(ActionOutcome Outcome, string? Message = null, IReadOnlyList<string>? Survivors = null);

/// <summary>Runs on/off actions. Only one action per entity at a time.</summary>
public sealed class ActionRunner
{
    private static readonly TimeSpan StopWait = TimeSpan.FromSeconds(3);

    private readonly EntityStore _store;
    private readonly StateLoop _stateLoop;
    private readonly ConfigStore _config;
    private readonly ILogger _log;

    public ActionRunner(EntityStore store, StateLoop stateLoop, ConfigStore config, ILogger log)
    {
        _store = store;
        _stateLoop = stateLoop;
        _config = config;
        _log = log;
    }

    public async Task<ActionResult> RunAsync(string id, bool on)
    {
        var entity = _store.Get(id);
        if (entity is null) return new ActionResult(ActionOutcome.NotFound, $"No entity with ID '{id}'");
        if (!entity.TryBeginAction()) return new ActionResult(ActionOutcome.Busy, $"An action is already running for '{id}'");

        try
        {
            return entity switch
            {
                ScriptEntity s => StartScript(s, on),
                GameEntity g when on => LaunchGame(g),
                GameEntity g => await StopGameAsync(g),
                _ => throw new InvalidOperationException("Unknown entity type"),
            };
        }
        catch (Exception ex)
        {
            entity.EndAction();
            _log.LogError("Action {Action} on {EntityId} failed: {Message}", on ? "on" : "off", id, ex.Message);
            return new ActionResult(ActionOutcome.Failed, ex.Message);
        }
    }

    private ActionResult StartScript(ScriptEntity s, bool on)
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            // stdout/stderr are deliberately NOT redirected: a launched GUI program would inherit
            // the pipe handles and reading to end-of-stream would hang until that program exits.
            WorkingDirectory = Path.GetDirectoryName(s.Path) ?? Environment.CurrentDirectory,
        };
        foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", s.Path, on ? "On" : "Off" })
            psi.ArgumentList.Add(a);

        var proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe did not start");
        var timeout = TimeSpan.FromSeconds(_config.Current.ScriptTimeoutSeconds);
        string action = on ? "on" : "off";

        _ = Task.Run(async () =>
        {
            try
            {
                using var cts = new CancellationTokenSource(timeout);
                await proc.WaitForExitAsync(cts.Token);
                _log.LogInformation("Script {EntityId} ({Action}) exited with code {ExitCode}", s.Id, action, proc.ExitCode);
            }
            catch (OperationCanceledException)
            {
                // Don't kill the process or its tree: it may be the program the script launched.
                _log.LogWarning("Script {EntityId} ({Action}) still running after {Seconds}s; no longer waiting (treated as started)",
                    s.Id, action, timeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                _log.LogWarning("Waiting for script {EntityId} failed: {Message}", s.Id, ex.Message);
            }
            finally
            {
                proc.Dispose();
                s.EndAction();
                _stateLoop.Refresh(s);
            }
        });

        return new ActionResult(ActionOutcome.Started);
    }

    private ActionResult LaunchGame(GameEntity g)
    {
        try
        {
            SteamGames.Launch(g.AppId);
            _log.LogInformation("Launched {EntityId} ({Name}) via steam://rungameid", g.Id, g.Name);
            return new ActionResult(ActionOutcome.Started);
        }
        finally
        {
            g.EndAction();
            _ = Task.Run(() => _stateLoop.Refresh(g));
        }
    }

    private async Task<ActionResult> StopGameAsync(GameEntity g)
    {
        var stop = Task.Run(() => SteamGames.Stop(g));
        _ = stop.ContinueWith(t =>
        {
            g.EndAction();
            if (t.IsFaulted)
                _log.LogError("Stopping {EntityId} failed: {Message}", g.Id, t.Exception?.GetBaseException().Message);
            else if (t.Result.Count > 0)
                _log.LogWarning("Stopping {EntityId}: processes survived: {Survivors}", g.Id, string.Join(", ", t.Result));
            else
                _log.LogInformation("Stopped {EntityId} ({Name})", g.Id, g.Name);
            _stateLoop.Refresh(g);
        }, TaskScheduler.Default);

        var finished = await Task.WhenAny(stop, Task.Delay(StopWait));
        if (finished == stop && stop.IsCompletedSuccessfully && stop.Result.Count > 0)
            return new ActionResult(ActionOutcome.Partial, Survivors: stop.Result);
        return new ActionResult(ActionOutcome.Started);
    }
}
