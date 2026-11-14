using System.Diagnostics;
using System.Net.NetworkInformation;
using System.ServiceProcess;

namespace PcControl.Agent.Checks;

public sealed record ProcessInfo(int Pid, string Name, string? Path);

/// <summary>
/// One view of the machine per state tick. Each source is read at most once, and only if a check needs it.
/// </summary>
public sealed class StateSnapshot : IDisposable
{
    private static readonly int SessionId = Process.GetCurrentProcess().SessionId;

    private readonly Lazy<List<ProcessInfo>> _processes;
    private readonly Lazy<HashSet<string>> _processNames;
    private readonly Lazy<ServiceController[]> _services;
    private readonly Lazy<HashSet<int>> _listeners;
    private readonly Lazy<List<string>> _windows;
    private bool _pathsLoaded;

    public StateSnapshot()
    {
        _processes = new Lazy<List<ProcessInfo>>(LoadProcesses);
        _processNames = new Lazy<HashSet<string>>(() =>
            new HashSet<string>(_processes.Value.Select(p => p.Name), StringComparer.OrdinalIgnoreCase));
        _services = new Lazy<ServiceController[]>(ServiceController.GetServices);
        _listeners = new Lazy<HashSet<int>>(() =>
            IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet());
        _windows = new Lazy<List<string>>(Native.GetVisibleWindowTitles);
    }

    /// <summary>Processes in the current user's session.</summary>
    public IReadOnlyList<ProcessInfo> Processes => _processes.Value;

    /// <summary>Processes in the current session, with executable paths resolved.</summary>
    public IReadOnlyList<ProcessInfo> ProcessesWithPaths
    {
        get
        {
            var list = _processes.Value;
            if (!_pathsLoaded)
            {
                for (int i = 0; i < list.Count; i++)
                    list[i] = list[i] with { Path = Native.GetProcessPath(list[i].Pid) };
                _pathsLoaded = true;
            }
            return list;
        }
    }

    public bool HasProcess(string nameWithoutExe) => _processNames.Value.Contains(nameWithoutExe);

    public ServiceController[] Services => _services.Value;

    public bool IsListening(int port) => _listeners.Value.Contains(port);

    public IReadOnlyList<string> WindowTitles => _windows.Value;

    private static List<ProcessInfo> LoadProcesses()
    {
        var result = new List<ProcessInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.SessionId == SessionId) result.Add(new ProcessInfo(p.Id, p.ProcessName, null));
            }
            catch (Exception)
            {
                // Process exited or is inaccessible.
            }
            finally
            {
                p.Dispose();
            }
        }
        return result;
    }

    public void Dispose()
    {
        if (_services.IsValueCreated)
            foreach (var s in _services.Value) s.Dispose();
    }
}
