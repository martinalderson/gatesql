using System.Security.Cryptography;
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
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    }) ?? new ProxyConfig();
}
else
{
    config = new ProxyConfig();
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
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_SSLMODE") is { } sslMode
    && Enum.TryParse<UpstreamSslMode>(sslMode, ignoreCase: true, out var parsedSslMode))
    config.Upstream.SslMode = parsedSslMode;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_SSL_CA_CERT") is { } caCert)
    config.Upstream.SslCaCertPath = caCert;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_SSL_CLIENT_CERT") is { } clientCert)
    config.Upstream.SslClientCertPath = clientCert;
if (Environment.GetEnvironmentVariable("GATESQL_UPSTREAM_SSL_CLIENT_KEY") is { } clientKey)
    config.Upstream.SslClientKeyPath = clientKey;
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
var isDevelopment = builder.Environment.IsDevelopment();
if (!isDevelopment)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; });
    builder.Logging.SetMinimumLevel(LogLevel.Warning);
    builder.Logging.AddFilter("DbProxy", LogLevel.Information);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.EntityFrameworkCore", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Error);
}

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

// Create full schema first (Sessions, QueryLogs, etc.), then add Settings table
await using (var initDb = await dbFactory.CreateDbContextAsync())
{
    await initDb.Database.EnsureCreatedAsync();
    // Migrate: add ExemptionReason column to QueryLogs (added in #49)
    try { await initDb.Database.ExecuteSqlRawAsync("ALTER TABLE \"QueryLogs\" ADD COLUMN \"ExemptionReason\" TEXT"); }
    catch { /* column already exists */ }
}
var settingsStore = new SettingsStore(dbFactory);
await settingsStore.InitializeAsync();
settingsStore.ApplyApiKeyToConfig(config);
settingsStore.ApplyUpstreamToConfig(config);

// First-run detection: no API keys after all config sources
var isFirstRun = config.Auth.ParentApiKeys.Count == 0;
var needsSetup = isFirstRun && !settingsStore.HasUpstreamConfig();
if (isFirstRun)
{
    var generatedKey = $"gatesql_pk_{Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant()}";
    config.Auth.ParentApiKeys = [new ParentApiKey { Name = "auto-generated", Key = generatedKey }];
    await settingsStore.SaveApiKey(generatedKey);
}

var setupState = new DbProxy.Dashboard.SetupState { SetupRequired = needsSetup };

var sessionManager = new SessionManager(TimeSpan.FromMinutes(config.Auth.IdleTimeoutMinutes), dbFactory);
await sessionManager.InitializeAsync();

var queryLogger = new QueryLogger(dbFactory);

// Register services for MVC DI
builder.Services.AddSingleton(config);
builder.Services.AddSingleton(jwtAuth);
builder.Services.AddSingleton(sessionManager);
builder.Services.AddSingleton(queryLogger);
builder.Services.AddSingleton(settingsStore);
builder.Services.AddSingleton(setupState);
builder.Services.AddControllersWithViews()
    .AddRazorOptions(options =>
    {
        options.ViewLocationFormats.Clear();
        options.ViewLocationFormats.Add("/Dashboard/Views/{1}/{0}.cshtml");
        options.ViewLocationFormats.Add("/Dashboard/Views/Shared/{0}.cshtml");
    });

var app = builder.Build();

// Static files (HTMX, SSE extension)
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
        Path.Combine(AppContext.BaseDirectory, "Dashboard", "wwwroot")),
});

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

// Print startup banner
Console.WriteLine();
Console.WriteLine(@"   ██████   █████  ████████ ███████ ┌──────────────────────────────┐");
Console.WriteLine(@"  ██       ██   ██    ██    ██      │ ███████  ██████  ██          │");
Console.WriteLine(@"  ██   ███ ███████    ██    █████   │ ██      ██    ██ ██          │");
Console.WriteLine(@"  ██    ██ ██   ██    ██    ██      │ ███████ ██    ██ ██          │");
Console.WriteLine(@"   ██████  ██   ██    ██    ███████ │      ██ ██ ██ ██ ██          │");
Console.WriteLine(@"                                    │ ███████  ██████  ███████     │");
Console.WriteLine(@"                                    └──────────────────────────────┘");
Console.WriteLine();
Console.WriteLine($"  Dashboard:  http://localhost:{config.Dashboard.Port}");
Console.WriteLine($"  Proxy:      localhost:{config.Proxy.ListenPort}");
var isDemo = Environment.GetEnvironmentVariable("GATESQL_DEMO") == "true";
if (isFirstRun || isDemo)
{
    Console.WriteLine($"  API Key:    {config.Auth.ParentApiKeys[0].Key}");
}
if (isFirstRun)
{
    Console.WriteLine();
    Console.WriteLine("  Open the dashboard to configure your database");
    Console.WriteLine("  and create your first agent session.");

    // Docker persistence warning
    if (Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true")
    {
        Console.WriteLine();
        Console.WriteLine("  \u26a0 Mount /app/data for persistence across restarts:");
        Console.WriteLine("    docker run -v gatesql-data:/app/data ...");
    }
}
Console.WriteLine();

// Run web host (blocks until shutdown)
await app.RunAsync(cts.Token);

// Cleanup
pgHandler.Dispose();
sessionManager.Dispose();
queryLogger.Dispose();
