using System.Collections.Concurrent;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Dashboard.Models;
using DbProxy.Data;
using DbProxy.Query;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace DbProxy.Dashboard.Controllers;

[ApiKeyAuth]
public class DashboardController : Controller
{
    private readonly SessionManager _sessionManager;
    private readonly JwtAuthenticator _jwtAuth;
    private readonly QueryLogger _queryLogger;
    private readonly ProxyConfig _config;
    private readonly SettingsStore _settingsStore;
    private readonly SetupState _setupState;

    public DashboardController(SessionManager sessionManager, JwtAuthenticator jwtAuth, QueryLogger queryLogger, ProxyConfig config, SettingsStore settingsStore, SetupState setupState)
    {
        _sessionManager = sessionManager;
        _jwtAuth = jwtAuth;
        _queryLogger = queryLogger;
        _config = config;
        _settingsStore = settingsStore;
        _setupState = setupState;
    }

    public IActionResult Index()
    {
        var model = BuildViewModel();
        return View(model);
    }

    [HttpGet("/dashboard/setup")]
    [SkipApiKeyAuth]
    public IActionResult Setup()
    {
        if (!_setupState.SetupRequired)
            return RedirectToAction("Login");

        return View(BuildSetupViewModel());
    }

    [HttpPost("/dashboard/setup/test")]
    [SkipApiKeyAuth]
    public async Task<IActionResult> SetupTest(string host, int port, string database, string username, string password)
    {
        try
        {
            var connStr = $"Host={host};Port={port};Database={database};Username={username};Password={password};Timeout=5";
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();
            await using var cmd = new NpgsqlCommand("SELECT version()", conn);
            var version = await cmd.ExecuteScalarAsync();
            return Content($"<div class=\"test-result success\">Connected — {version}</div>", "text/html");
        }
        catch (Exception ex)
        {
            return Content($"<div class=\"test-result failure\">Connection failed: {ex.Message}</div>", "text/html");
        }
    }

    [HttpPost("/dashboard/setup")]
    [SkipApiKeyAuth]
    public async Task<IActionResult> SetupSave(string host, int port, string database, string username, string password, string sslMode)
    {
        if (!Enum.TryParse<UpstreamSslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
            parsedSslMode = UpstreamSslMode.Disable;

        var upstream = new UpstreamSettings
        {
            Host = host,
            Port = port,
            Database = database,
            Username = username,
            Password = password,
            SslMode = parsedSslMode,
        };

        // Save to SQLite
        await _settingsStore.SaveUpstreamConfig(upstream);

        // Hot-reload in-memory config
        _config.Upstream.Host = upstream.Host;
        _config.Upstream.Port = upstream.Port;
        _config.Upstream.Database = upstream.Database;
        _config.Upstream.Username = upstream.Username;
        _config.Upstream.Password = upstream.Password;
        _config.Upstream.SslMode = upstream.SslMode;

        // Setup complete
        _setupState.SetupRequired = false;

        return RedirectToAction("Login");
    }

    private SetupViewModel BuildSetupViewModel()
    {
        return new SetupViewModel
        {
            ApiKey = _config.Auth.ParentApiKeys.FirstOrDefault()?.Key ?? "",
            ProxyPort = _config.Proxy.ListenPort,
            DashboardPort = _config.Dashboard.Port,
            IsDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true",
        };
    }

    [HttpGet("/dashboard/login")]
    [SkipApiKeyAuth]
    public IActionResult Login() => View();

    [HttpPost("/dashboard/login")]
    [SkipApiKeyAuth]
    public IActionResult LoginPost(string apiKey)
    {
        if (string.IsNullOrEmpty(apiKey) || !_config.Auth.ParentApiKeys.Any(k => k.Key == apiKey))
        {
            ViewBag.Error = "Invalid API key";
            return View("Login");
        }

        Response.Cookies.Append("gatesql_key", apiKey, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            MaxAge = TimeSpan.FromDays(30),
        });

        return RedirectToAction("Index");
    }

    [HttpPost("/dashboard/logout")]
    public IActionResult Logout()
    {
        Response.Cookies.Delete("gatesql_key");
        return RedirectToAction("Login");
    }

    [HttpGet("/dashboard/sessions/new")]
    public IActionResult CreateSessionForm()
    {
        return PartialView("_CreateSessionForm");
    }

    [HttpPost("/dashboard/sessions/create")]
    public IActionResult CreateSession(string agentId, string task, int? queryBudget, bool readOnly, string dangerousQueryMode, string? allowedTables)
    {
        var sessionId = $"sess_{Guid.NewGuid():N}";
        var lifetime = TimeSpan.FromMinutes(_config.Auth.HardCapMinutes);
        var expiresAt = DateTime.UtcNow.Add(lifetime);

        var parsedTables = string.IsNullOrWhiteSpace(allowedTables)
            ? null
            : allowedTables.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        _sessionManager.CreateSession(
            sessionId, agentId, task, queryBudget, expiresAt,
            readOnly, dangerousQueryMode ?? "block", parsedTables);

        var token = _jwtAuth.GenerateToken(sessionId, agentId, task, queryBudget, lifetime);

        var host = _config.Proxy.ListenHost == "0.0.0.0" ? "localhost" : _config.Proxy.ListenHost;
        var port = _config.Proxy.ListenPort;
        var database = _config.Upstream.Database;

        ViewBag.SessionId = sessionId;
        ViewBag.Token = token;
        ViewBag.ExpiresAt = expiresAt;
        ViewBag.ConnectionString = $"postgresql://agent:{Uri.EscapeDataString(token)}@{host}:{port}/{database}";
        ViewBag.PsqlCommand = $"PGPASSWORD=\"{token}\" psql -h {host} -p {port} -U agent -d {database}";

        return PartialView("_SessionCreated");
    }

    [HttpPost("/dashboard/revoke/{sessionId}")]
    public IActionResult Revoke(string sessionId)
    {
        _sessionManager.RevokeSession(sessionId);
        var model = BuildViewModel();
        return PartialView("_SessionsTable", model);
    }

    [HttpGet("/dashboard/events")]
    public async Task Events(CancellationToken ct)
    {
        Response.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers.Connection = "keep-alive";

        // Subscribe to new query events
        var queryQueue = new ConcurrentQueue<QueryLogEntry>();
        void OnQuery(QueryLogEntry entry) => queryQueue.Enqueue(entry);
        _queryLogger.OnQueryLogged += OnQuery;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // Push stats + sessions every 2s
                var model = BuildViewModel();
                var statsHtml = await RenderPartialAsync("_Stats", model);
                var sessionsHtml = await RenderPartialAsync("_SessionsTable", model);

                await WriteSseEvent("stats", statsHtml, ct);
                await WriteSseEvent("sessions", sessionsHtml, ct);

                // Push any queued query events
                while (queryQueue.TryDequeue(out var entry))
                {
                    var queryHtml = await RenderPartialAsync("_QueryRow", entry);
                    await WriteSseEvent("query", queryHtml, ct);
                }

                await Response.Body.FlushAsync(ct);
                await Task.Delay(2000, ct);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _queryLogger.OnQueryLogged -= OnQuery;
        }
    }

    private DashboardViewModel BuildViewModel()
    {
        var allSessions = _sessionManager.GetAllSessions();
        var activeSessions = allSessions
            .Where(s => !s.IsRevoked && DateTime.UtcNow < s.ExpiresAt)
            .Select(SessionViewModel.FromSession)
            .ToList();

        return new DashboardViewModel
        {
            ActiveSessions = activeSessions,
            RecentQueries = _queryLogger.GetRecentQueries(50).ToList(),
            TotalSessions = allSessions.Count,
            ConnectedNow = allSessions.Count(s => s.IsConnected),
            TotalQueries = allSessions.Sum(s => s.QueriesUsed),
            IdleTimeoutMinutes = _config.Auth.IdleTimeoutMinutes,
        };
    }

    private async Task<string> RenderPartialAsync(string viewName, object model)
    {
        ViewData.Model = model;
        using var writer = new StringWriter();
        var viewEngine = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Mvc.ViewEngines.ICompositeViewEngine>();
        var viewResult = viewEngine.FindView(ControllerContext, viewName, false);
        if (!viewResult.Success)
            return $"<!-- view {viewName} not found -->";

        var viewContext = new Microsoft.AspNetCore.Mvc.Rendering.ViewContext(
            ControllerContext, viewResult.View, ViewData, TempData, writer, new Microsoft.AspNetCore.Mvc.ViewFeatures.HtmlHelperOptions());
        await viewResult.View.RenderAsync(viewContext);
        return writer.ToString();
    }

    private async Task WriteSseEvent(string eventName, string data, CancellationToken ct)
    {
        // SSE format: each data line must be prefixed with "data: "
        var lines = data.Replace("\r\n", "\n").Split('\n');
        await Response.WriteAsync($"event: {eventName}\n", ct);
        foreach (var line in lines)
            await Response.WriteAsync($"data: {line}\n", ct);
        await Response.WriteAsync("\n", ct);
    }
}

[AttributeUsage(AttributeTargets.Method)]
public class SkipApiKeyAuthAttribute : Attribute { }
