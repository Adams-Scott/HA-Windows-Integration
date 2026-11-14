using System.Diagnostics;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Api;
using PcControl.Agent.Core;

namespace PcControl.Agent.Tray;

/// <summary>The tray icon and its menu. Everything here runs on the UI thread.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly AgentCore _core;
    private readonly ApiHost _api;
    private readonly string _logsDir;
    private readonly ILogger _log;
    private readonly SynchronizationContext _ui;
    private readonly NotifyIcon _tray;
    private readonly Icon _okIcon = TrayIcons.Create(error: false);
    private readonly Icon _errorIcon = TrayIcons.Create(error: true);
    private readonly ToolStripMenuItem _apiItem;
    private readonly ToolStripMenuItem _rescanItem;
    private readonly ToolStripMenuItem _startupItem;

    public TrayContext(AgentCore core, ApiHost api, string logsDir, ILogger log)
    {
        _core = core;
        _api = api;
        _logsDir = logsDir;
        _log = log;
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        var menu = new ContextMenuStrip();
        var version = AgentCore.Version.Split('.');
        menu.Items.Add(new ToolStripMenuItem($"PC Control v{version[0]}.{(version.Length > 1 ? version[1] : "0")}") { Enabled = false });
        _apiItem = new ToolStripMenuItem("API: starting…") { Enabled = false };
        menu.Items.Add(_apiItem);
        menu.Items.Add(new ToolStripSeparator());
        _rescanItem = new ToolStripMenuItem("Rescan", null, (_, _) => Rescan());
        menu.Items.Add(_rescanItem);
        menu.Items.Add(new ToolStripMenuItem("Open scripts folder", null, (_, _) => OpenScriptsFolder()));
        menu.Items.Add(new ToolStripMenuItem("Copy API token", null, (_, _) => CopyToken()));
        menu.Items.Add(new ToolStripMenuItem("Open settings", null, (_, _) => OpenSettings()));
        menu.Items.Add(new ToolStripMenuItem("Open log folder", null, (_, _) => OpenFolder(_logsDir)));
        menu.Items.Add(new ToolStripSeparator());
        _startupItem = new ToolStripMenuItem("Start with Windows", null, (_, _) => ToggleStartup()) { CheckOnClick = false };
        menu.Items.Add(_startupItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Exit", null, (_, _) => ExitThread()));
        menu.Opening += (_, _) => RefreshMenu();

        _tray = new NotifyIcon { ContextMenuStrip = menu, Icon = _okIcon, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) ShowBalloon(StatusText());
        };

        _core.Rescanned += (_, _) => _ui.Post(_ => RefreshStatus(), null);
        _api.StatusChanged += () => _ui.Post(_ => RefreshStatus(), null);
        RefreshStatus();
    }

    private string StatusText() =>
        $"PC Control: {_core.Store.ScriptCount} scripts, {_core.Store.GameCount} games";

    private void RefreshStatus()
    {
        string text = StatusText();
        if (_api.LastError is not null) text += "\nAPI error: " + _api.LastError;
        _tray.Text = text.Length > 127 ? text[..127] : text;
        _tray.Icon = _api.IsRunning ? _okIcon : _errorIcon;
        RefreshMenu();
    }

    private void RefreshMenu()
    {
        _apiItem.Text = _api.IsRunning ? $"API: {_api.Url}" : $"API: not running ({_api.LastError ?? "stopped"})";
        _startupItem.Checked = AutoStart.IsEnabled;
    }

    private void Rescan()
    {
        _rescanItem.Enabled = false;
        Task.Run(() =>
        {
            try
            {
                var (scripts, games) = _core.Rescan();
                _ui.Post(_ => ShowBalloon($"Found {scripts} scripts, {games} games"), null);
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Rescan failed");
                _ui.Post(_ => ShowBalloon("Rescan failed: " + ex.Message, ToolTipIcon.Error), null);
            }
            finally
            {
                _ui.Post(_ => _rescanItem.Enabled = true, null);
            }
        });
    }

    private void OpenScriptsFolder()
    {
        var folder = _core.Config.Current.ScriptsFolderExpanded;
        try { Directory.CreateDirectory(folder); } catch { /* reported by OpenFolder */ }
        OpenFolder(folder);
    }

    private void CopyToken()
    {
        try
        {
            Clipboard.SetText(_core.Config.Current.ApiToken);
            ShowBalloon("API token copied to the clipboard");
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not copy the token: " + ex.Message, ToolTipIcon.Error);
        }
    }

    private void OpenSettings()
    {
        var path = _core.Config.FilePath;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch
        {
            // No app is associated with .json.
            try { Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { ShowBalloon("Could not open settings: " + ex.Message, ToolTipIcon.Error); }
        }
    }

    private void OpenFolder(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex)
        {
            ShowBalloon($"Could not open {folder}: {ex.Message}", ToolTipIcon.Error);
        }
    }

    private void ToggleStartup()
    {
        try
        {
            AutoStart.Set(!AutoStart.IsEnabled);
            _log.LogInformation("Start with Windows: {Enabled}", AutoStart.IsEnabled);
        }
        catch (Exception ex)
        {
            ShowBalloon("Could not change startup setting: " + ex.Message, ToolTipIcon.Error);
        }
        RefreshMenu();
    }

    private void ShowBalloon(string text, ToolTipIcon icon = ToolTipIcon.Info) =>
        _tray.ShowBalloonTip(3000, "PC Control", text, icon);

    protected override void ExitThreadCore()
    {
        _tray.Visible = false;
        base.ExitThreadCore();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _tray.Dispose();
            _okIcon.Dispose();
            _errorIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}
