using Microsoft.Extensions.Logging;
using PcControl.Agent.Checks;
using PcControl.Agent.Config;
using PcControl.Agent.Entities;

namespace PcControl.Agent.Core;

/// <summary>Refreshes every entity's cached state on a fixed interval. API reads never trigger a check.</summary>
public sealed class StateLoop : IDisposable
{
    private readonly EntityStore _store;
    private readonly StateEvaluator _evaluator;
    private readonly ConfigStore _config;
    private readonly ILogger _log;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _tickLock = new(1, 1);
    private Task? _loop;

    public StateLoop(EntityStore store, StateEvaluator evaluator, ConfigStore config, ILogger log)
    {
        _store = store;
        _evaluator = evaluator;
        _config = config;
        _log = log;
    }

    public void Start() => _loop = Task.Run(RunAsync);

    private async Task RunAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_config.Current.StateIntervalSeconds), _cts.Token);
                Tick();
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { _log.LogError(ex, "State loop tick failed"); }
        }
    }

    /// <summary>Evaluates every entity against one snapshot.</summary>
    public void Tick()
    {
        _tickLock.Wait();
        try
        {
            using var snap = new StateSnapshot();
            foreach (var e in _store.All)
                e.State = _evaluator.Evaluate(e, snap);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    /// <summary>Re-checks one entity now, e.g. right after its script returns.</summary>
    public void Refresh(Entity entity)
    {
        _tickLock.Wait();
        try
        {
            using var snap = new StateSnapshot();
            entity.State = _evaluator.Evaluate(entity, snap);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Refresh failed for {EntityId}", entity.Id);
        }
        finally
        {
            _tickLock.Release();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        _cts.Dispose();
    }
}
