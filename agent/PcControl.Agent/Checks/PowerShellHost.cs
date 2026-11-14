using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace PcControl.Agent.Checks;

/// <summary>
/// Evaluates <c>ps:</c> check expressions in one long-lived, hidden powershell.exe (a reused runspace),
/// so each check doesn't pay PowerShell's start-up cost. A timeout kills the host; the next call starts a new one.
/// </summary>
public sealed class PowerShellHost : IDisposable
{
    private const string Marker = "##PCC##";

    // Reads "<id>:<base64 utf8 expression>" lines, writes "##PCC##<id>:R:True|False" or "##PCC##<id>:E:<msg>".
    private const string LoopScript = """
        $ProgressPreference = 'SilentlyContinue'
        while ($true) {
          $line = [Console]::In.ReadLine()
          if ($null -eq $line) { break }
          $line = $line.Trim([char]0xFEFF, ' ')
          $sep = $line.IndexOf(':')
          if ($sep -lt 1) { continue }
          $id = $line.Substring(0, $sep)
          try {
            $expr = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($line.Substring($sep + 1)))
            $out = @(Invoke-Expression $expr)
            $last = if ($out.Count) { $out[-1] } else { $null }
            [Console]::Out.WriteLine('##PCC##' + $id + ':R:' + [bool](($last -is [bool]) -and $last))
          } catch {
            [Console]::Out.WriteLine('##PCC##' + $id + ':E:' + ($_.Exception.Message -replace '\r?\n', ' '))
          }
          [Console]::Out.Flush()
        }
        """;

    private readonly ILogger _log;
    private readonly object _lock = new();
    private Process? _proc;
    private long _nextId;

    public PowerShellHost(ILogger log) => _log = log;

    /// <summary>Returns the expression's truth value, or null on error or timeout.</summary>
    public bool? Evaluate(string expression, TimeSpan timeout)
    {
        lock (_lock)
        {
            try
            {
                var proc = EnsureStarted();
                string id = (++_nextId).ToString();
                proc.StandardInput.WriteLine($"{id}:{Convert.ToBase64String(Encoding.UTF8.GetBytes(expression))}");
                proc.StandardInput.Flush();

                var deadline = DateTime.UtcNow + timeout;
                string prefix = $"{Marker}{id}:";
                while (true)
                {
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) break;
                    var read = proc.StandardOutput.ReadLineAsync();
                    if (!read.Wait(remaining)) break;
                    var line = read.Result;
                    if (line is null) { Kill(); return null; }
                    if (!line.StartsWith(prefix, StringComparison.Ordinal)) continue; // stray output or a stale answer
                    var rest = line[prefix.Length..];
                    if (rest.StartsWith("R:", StringComparison.Ordinal))
                        return rest[2..].Equals("True", StringComparison.OrdinalIgnoreCase);
                    _log.LogDebug("ps: check '{Expression}' failed: {Error}", expression, rest.Length > 2 ? rest[2..] : rest);
                    return null;
                }

                _log.LogWarning("ps: check '{Expression}' timed out after {Seconds}s; restarting PowerShell host",
                    expression, timeout.TotalSeconds);
                Kill();
                return null;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "ps: check '{Expression}' failed", expression);
                Kill();
                return null;
            }
        }
    }

    private Process EnsureStarted()
    {
        if (_proc is { HasExited: false }) return _proc;
        _proc?.Dispose();

        var psi = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-EncodedCommand",
                     Convert.ToBase64String(Encoding.Unicode.GetBytes(LoopScript)) })
            psi.ArgumentList.Add(a);

        _proc = Process.Start(psi) ?? throw new InvalidOperationException("powershell.exe did not start");
        return _proc;
    }

    private void Kill()
    {
        if (_proc is null) return;
        try { if (!_proc.HasExited) _proc.Kill(entireProcessTree: true); } catch { /* already gone */ }
        _proc.Dispose();
        _proc = null;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            try { _proc?.StandardInput.Close(); } catch { /* ignore */ }
            Kill();
        }
    }
}
