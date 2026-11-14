using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using PcControl.Agent.Config;
using PcControl.Agent.Core;
using PcControl.Agent.Entities;
using Serilog;
using ILogger = Microsoft.Extensions.Logging.ILogger;

namespace PcControl.Agent.Api;

public sealed record ErrorBody(string Error, string Message);

public sealed record EntityDto(string Id, string Kind, string Name, string Icon, string State, bool Busy)
{
    public static EntityDto From(Entity e) => new(e.Id, e.Kind, e.Name, e.Icon, e.State, e.Busy);
}

/// <summary>Kestrel minimal API running in the tray process.</summary>
public sealed class ApiHost : IAsyncDisposable
{
    private const string Prefix = "/api/v1";
    private readonly AgentCore _core;
    private readonly ILogger _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private WebApplication? _app;

    public ApiHost(AgentCore core, ILogger log)
    {
        _core = core;
        _log = log;
    }

    public bool IsRunning => _app is not null;
    public string? LastError { get; private set; }
    public string? Url { get; private set; }

    public event Action? StatusChanged;

    public async Task StartAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopCoreAsync();
            var cfg = _core.Config.Current;
            try
            {
                var app = Build(cfg);
                await app.StartAsync();
                _app = app;
                LastError = null;
                Url = $"http://{NetworkInfo.DisplayAddress(cfg.BindAddress)}:{cfg.Port}";
                _log.LogInformation("API listening on {Bind}:{Port}", cfg.BindAddress, cfg.Port);
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                Url = null;
                _log.LogError("API failed to bind {Bind}:{Port}: {Message}", cfg.BindAddress, cfg.Port, LastError);
            }
        }
        finally
        {
            _lifecycle.Release();
            StatusChanged?.Invoke();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _lifecycle.Release(); }
    }

    private async Task StopCoreAsync()
    {
        if (_app is null) return;
        try
        {
            await _app.StopAsync(TimeSpan.FromSeconds(3));
            await _app.DisposeAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning("Error stopping API: {Message}", ex.Message);
        }
        _app = null;
    }

    private WebApplication Build(AgentConfig cfg)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production,
        });
        builder.Logging.ClearProviders();
        builder.Logging.AddSerilog(Log.Logger);
        builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            o.SerializerOptions.DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
        });
        builder.WebHost.ConfigureKestrel(o =>
        {
            var address = cfg.BindAddress is "*" or "+" ? IPAddress.Any : IPAddress.Parse(cfg.BindAddress);
            o.Listen(address, cfg.Port);
            o.AddServerHeader = false;
        });

        var app = builder.Build();
        app.Use(AuthMiddleware);
        MapEndpoints(app);
        return app;
    }

    private async Task AuthMiddleware(HttpContext ctx, Func<Task> next)
    {
        if (ctx.Request.Path.Equals(Prefix + "/health", StringComparison.OrdinalIgnoreCase))
        {
            await next();
            return;
        }

        if (!IsAuthorized(ctx.Request.Headers.Authorization.ToString(), _core.Config.Current.ApiToken))
        {
            _log.LogWarning("Unauthorized {Method} {Path} from {Remote}", ctx.Request.Method, ctx.Request.Path,
                ctx.Connection.RemoteIpAddress);
            await Results.Json(new ErrorBody("unauthorized", "Missing or invalid API token"), statusCode: 401).ExecuteAsync(ctx);
            return;
        }
        await next();
    }

    /// <summary>Constant-time comparison of "Bearer &lt;token&gt;" against the configured token.</summary>
    public static bool IsAuthorized(string header, string expected)
    {
        const string scheme = "Bearer ";
        if (string.IsNullOrEmpty(expected) || !header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        var given = header[scheme.Length..].Trim();
        // Hash both sides so the comparison is constant-time regardless of length.
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(given)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
    }

    private void MapEndpoints(WebApplication app)
    {
        var api = app.MapGroup(Prefix);

        api.MapGet("/health", () => Results.Json(new { status = "ok", version = AgentCore.Version }));

        api.MapGet("/info", () => Results.Json(new
        {
            hostname = Environment.MachineName,
            agentVersion = AgentCore.Version,
            interfaces = NetworkInfo.GetInterfaces().Select(i => new { name = i.Name, mac = i.Mac, ipv4 = i.Ipv4, wired = i.Wired }),
        }));

        api.MapGet("/entities", () =>
        {
            var (entities, revision) = _core.Store.Snapshot();
            return Results.Json(new { revision, entities = entities.Select(EntityDto.From) });
        });

        api.MapGet("/entities/{id}", (string id) =>
            _core.Store.Get(id) is { } e
                ? Results.Json(EntityDto.From(e))
                : Results.Json(new ErrorBody("not_found", $"No entity with ID '{id}'"), statusCode: 404));

        api.MapPost("/entities/{id}/on", (string id) => RunAction(id, true));
        api.MapPost("/entities/{id}/off", (string id) => RunAction(id, false));

        api.MapPost("/rescan", () =>
        {
            var (scripts, games) = _core.Rescan();
            _log.LogInformation("API rescan: {Scripts} scripts, {Games} games", scripts, games);
            return Results.Json(new { scripts, games });
        });

        api.MapPost("/power/shutdown", () =>
        {
            try
            {
                int delay = _core.Shutdown();
                _log.LogInformation("API action power/shutdown: started");
                return Results.Json(new { delaySeconds = delay }, statusCode: 202);
            }
            catch (Exception ex)
            {
                _log.LogError("API action power/shutdown failed: {Message}", ex.Message);
                return Results.Json(new ErrorBody("action_failed", ex.Message), statusCode: 500);
            }
        });
    }

    private async Task<IResult> RunAction(string id, bool on)
    {
        string action = on ? "on" : "off";
        var r = await _core.Actions.RunAsync(id, on);
        _log.LogInformation("API action {Action} on {EntityId}: {Result}", action, id, r.Outcome.ToString().ToLowerInvariant());
        return r.Outcome switch
        {
            ActionOutcome.Started => Results.Json(new { id, action, result = "started" }, statusCode: 202),
            ActionOutcome.Partial => Results.Json(new { id, action, result = "partial", survivors = r.Survivors }, statusCode: 202),
            ActionOutcome.NotFound => Results.Json(new ErrorBody("not_found", r.Message ?? "Not found"), statusCode: 404),
            ActionOutcome.Busy => Results.Json(new ErrorBody("busy", r.Message ?? "Busy"), statusCode: 409),
            _ => Results.Json(new ErrorBody("action_failed", r.Message ?? "Action failed"), statusCode: 500),
        };
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
