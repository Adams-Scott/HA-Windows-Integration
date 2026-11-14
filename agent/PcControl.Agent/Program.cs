using Microsoft.Extensions.Logging;
using PcControl.Agent.Api;
using PcControl.Agent.Checks;
using PcControl.Agent.Config;
using PcControl.Agent.Core;
using PcControl.Agent.Entities;
using PcControl.Agent.Scripts;
using PcControl.Agent.Steam;
using PcControl.Agent.Tray;
using Serilog;
using Serilog.Extensions.Logging;

namespace PcControl.Agent;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using var mutex = new Mutex(initiallyOwned: true, @"Local\PcControl.Agent.SingleInstance", out bool isFirst);
        if (!isFirst) return 0; // Another instance is running: exit silently.

        // PCCONTROL_DATA_DIR overrides %APPDATA%\PcControl (used for development and testing).
        var root = Environment.GetEnvironmentVariable("PCCONTROL_DATA_DIR") is { Length: > 0 } overrideDir
            ? overrideDir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PcControl");
        var logsDir = Path.Combine(root, "logs");
        Directory.CreateDirectory(logsDir);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(logsDir, "agent-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        using var loggerFactory = new SerilogLoggerFactory(Log.Logger);
        var log = loggerFactory.CreateLogger("PcControl");

        try
        {
            ApplicationConfiguration.Initialize();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) => log.LogError(e.Exception, "Unhandled UI exception");
            AppDomain.CurrentDomain.UnhandledException += (_, e) => log.LogCritical(e.ExceptionObject as Exception, "Unhandled exception");
            TaskScheduler.UnobservedTaskException += (_, e) => { log.LogWarning(e.Exception, "Unobserved task exception"); e.SetObserved(); };

            log.LogInformation("PC Control Agent {Version} starting", AgentCore.Version);

            using var config = new ConfigStore(Path.Combine(root, "config.json"));
            config.AttachLogger(log);
            bool firstRun = config.LoadOrCreate();
            if (firstRun)
            {
                log.LogInformation("First run: created {Path}", config.FilePath);
                try { AutoStart.Set(true); } catch (Exception ex) { log.LogWarning("Could not enable start with Windows: {Message}", ex.Message); }
            }
            else
            {
                try { AutoStart.RefreshPathIfEnabled(); } catch { /* not important */ }
            }

            using var ps = new PowerShellHost(log);
            var store = new EntityStore();
            using var stateLoop = new StateLoop(store, new StateEvaluator(ps, log), config, log);
            var actions = new ActionRunner(store, stateLoop, config, log);
            var core = new AgentCore(config, store, stateLoop, actions, new ScriptScanner(log), new SteamScanner(log), log);

            // Startup order: config, scripts, Steam, state loop, then Kestrel.
            core.Rescan();
            stateLoop.Start();
            var api = new ApiHost(core, log);
            api.StartAsync().GetAwaiter().GetResult();

            config.Changed += (old, next) =>
            {
                if (old.Port != next.Port || old.BindAddress != next.BindAddress)
                    _ = Task.Run(api.StartAsync);
                if (old.ScriptsFolder != next.ScriptsFolder ||
                    System.Text.Json.JsonSerializer.Serialize(old.Steam) != System.Text.Json.JsonSerializer.Serialize(next.Steam))
                    _ = Task.Run(core.Rescan);
            };
            config.StartWatching();

            using (var tray = new TrayContext(core, api, logsDir, log))
            {
                Application.Run(tray);
            }

            log.LogInformation("Exiting");
            api.StopAsync().GetAwaiter().GetResult();
            return 0;
        }
        catch (Exception ex)
        {
            log.LogCritical(ex, "Fatal error");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
