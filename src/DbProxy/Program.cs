using System.Text.Json;
using DbProxy.Api;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Data;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.EntityFrameworkCore;

// Load config
var configPath = args.Length > 0 ? args[0] : "config.json";
ProxyConfig config;

if (File.Exists(configPath))
{
    var json = File.ReadAllText(configPath);
    config = JsonSerializer.Deserialize<ProxyConfig>(json, new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true,
    }) ?? new ProxyConfig();
}
else
{
    config = new ProxyConfig();
    var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
    File.WriteAllText(configPath, json);
    Console.WriteLine($"Created default config at {configPath}");
}

// Apply environment variable overrides
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_HOST") is { } host)
    config.Upstream.Host = host;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_PORT") is { } port && int.TryParse(port, out var p))
    config.Upstream.Port = p;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_USER") is { } user)
    config.Upstream.Username = user;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_PASSWORD") is { } pass)
    config.Upstream.Password = pass;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_DATABASE") is { } db)
    config.Upstream.Database = db;
if (Environment.GetEnvironmentVariable("GATESQL_API_KEY") is { } apiKey)
    config.Auth.ParentApiKeys = [new ParentApiKey { Name = "env", Key = apiKey }];

if (Environment.GetEnvironmentVariable("GATESQL_DB_CONNECTION") is { } dbConn)
    config.Storage.ConnectionString = dbConn;

// Initialize services
var signingKeyManager = new SigningKeyManager(config.Auth.SigningKeyPath);
var jwtAuth = new JwtAuthenticator(signingKeyManager);

// Build web app for admin API + dashboard
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{config.Dashboard.Port}");
builder.Logging.SetMinimumLevel(LogLevel.Information);

// EF Core + SQLite
builder.Services.AddDbContextFactory<GateSqlDbContext>(options =>
    options.UseSqlite(config.Storage.ConnectionString));

// Ensure DB directory exists for SQLite
var dbPath = config.Storage.ConnectionString
    .Split(';')
    .Select(p => p.Trim())
    .FirstOrDefault(p => p.StartsWith("Data Source=", StringComparison.OrdinalIgnoreCase))
    ?["Data Source=".Length..];
if (dbPath != null)
{
    var dir = Path.GetDirectoryName(dbPath);
    if (!string.IsNullOrEmpty(dir))
        Directory.CreateDirectory(dir);
}

var sp = builder.Services.BuildServiceProvider();
var dbFactory = sp.GetRequiredService<IDbContextFactory<GateSqlDbContext>>();

var sessionManager = new SessionManager(TimeSpan.FromMinutes(config.Auth.IdleTimeoutMinutes), dbFactory);
await sessionManager.InitializeAsync();

var queryLogger = new QueryLogger(dbFactory);

// Register services for MVC DI
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(sessionManager);
builder.Services.AddSingleton(queryLogger);
builder.Services.AddControllersWithViews()
    .AddRazorOptions(options =>
    {
        options.ViewLocationFormats.Clear();
        options.ViewLocationFormats.Add("/Dashboard/Views/{1}/{0}.cshtml");
        options.ViewLocationFormats.Add("/Dashboard/Views/Shared/{0}.cshtml");
    });

var app = builder.Build();

// Admin API
app.MapAdminApi(config, jwtAuth, sessionManager, queryLogger);

// Dashboard MVC
if (config.Dashboard.Enabled)
{
    app.MapControllerRoute(
        name: "default",
        pattern: "{controller=Dashboard}/{action=Index}/{id?}");
}

// Start PG proxy in background
var pgHandler = new PgProtocolHandler(config, jwtAuth, sessionManager, queryLogger,
    app.Services.GetRequiredService<ILogger<PgProtocolHandler>>());

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var proxyTask = Task.Run(() => pgHandler.StartAsync(cts.Token));

Console.WriteLine($"Admin API + Dashboard: http://localhost:{config.Dashboard.Port}");
Console.WriteLine($"PG Proxy: localhost:{config.Proxy.ListenPort}");

// Run web host (blocks until shutdown)
await app.RunAsync(cts.Token);

// Cleanup
pgHandler.Dispose();
sessionManager.Dispose();
queryLogger.Dispose();
