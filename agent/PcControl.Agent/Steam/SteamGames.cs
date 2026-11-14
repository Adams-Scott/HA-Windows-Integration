using System.Diagnostics;
using Microsoft.Win32;
using PcControl.Agent.Checks;
using PcControl.Agent.Entities;

namespace PcControl.Agent.Steam;

/// <summary>Start, state and stop for Steam games.</summary>
public static class SteamGames
{
    public static void Launch(uint appId) =>
        Process.Start(new ProcessStartInfo($"steam://rungameid/{appId}") { UseShellExecute = true })?.Dispose();

    public static bool IsRunning(GameEntity game, StateSnapshot snap)
    {
        string prefix = game.InstallPrefix;
        if (snap.ProcessesWithPaths.Any(p => p.Path is not null && p.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            return true;
        return RegistryRunningFlag(game.AppId);
    }

    /// <summary>HKCU\Software\Valve\Steam\Apps\&lt;appid&gt;\Running = 1, set by Steam while a game runs.</summary>
    public static bool RegistryRunningFlag(uint appId)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"Software\Valve\Steam\Apps\{appId}");
        return key?.GetValue("Running") is int i && i == 1;
    }

    /// <summary>
    /// Kills every process in this session whose executable is under the game's install folder.
    /// Returns the names of processes that could not be killed.
    /// </summary>
    public static List<string> Stop(GameEntity game)
    {
        string prefix = game.InstallPrefix;
        var survivors = new List<string>();
        var killed = new List<Process>();
        int session = Process.GetCurrentProcess().SessionId;

        foreach (var p in Process.GetProcesses())
        {
            bool keep = false;
            try
            {
                if (p.SessionId != session) continue;
                var path = Native.GetProcessPath(p.Id);
                if (path is null || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    p.Kill();
                    killed.Add(p);
                    keep = true;
                }
                catch (Exception)
                {
                    survivors.Add(SafeName(p, path));
                }
            }
            catch (Exception)
            {
                // Exited while we looked at it.
            }
            finally
            {
                if (!keep) p.Dispose();
            }
        }

        var deadline = DateTime.UtcNow.AddSeconds(2.5);
        foreach (var p in killed)
        {
            try
            {
                var left = deadline - DateTime.UtcNow;
                if (!p.WaitForExit(left > TimeSpan.Zero ? left : TimeSpan.Zero) && !p.HasExited)
                    survivors.Add(SafeName(p, null));
            }
            catch (Exception) { /* gone */ }
            finally { p.Dispose(); }
        }

        return survivors.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string SafeName(Process p, string? path)
    {
        if (path is not null) return Path.GetFileName(path);
        try { return p.ProcessName + ".exe"; } catch { return $"pid {p.Id}"; }
    }
}
