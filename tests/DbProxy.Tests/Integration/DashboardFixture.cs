using System.Net;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Dashboard;
using DbProxy.Dashboard.Controllers;
using DbProxy.Data;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class DashboardFixture : IAsyncLifetime
{
    public ProxyConfig Config { get; private set; } = null!;
    public int ProxyPort { get; private set; }
    public int ApiPort { get; private set; }
    public string ApiKey => "test_key_123";

    private PostgreSqlContainer _pg = null!;
    private PgProtocolHandler _pgHandler = null!;
    private WebApplication _webApp = null!;
    private JwtAuthenticator _jwtAuth = null!;
    private SessionManager _sessionManager = null!;
    private QueryLogger _queryLogger = null!;
    private CancellationTokenSource _cts = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:17")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
        await _pg.StartAsync();

        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-dashboard-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        ProxyPort = GetFreePort();
        ApiPort = GetFreePort();

        Config = new ProxyConfig
        {
            Proxy = new ProxySettings { ListenPort = ProxyPort, ListenHost = "0.0.0.0" },
            Upstream = new UpstreamSettings
            {
                Host = _pg.Hostname,
                Port = _pg.GetMappedPublicPort(5432),
                Database = "postgres",
                Username = "testuser",
                Password = "testpass",
            },
            Auth = new AuthSettings
            {
                HardCapMinutes = 60,
                IdleTimeoutMinutes = 5,
                SigningKeyPath = Path.Combine(_tempDir, "signing.key"),
                ParentApiKeys = [new ParentApiKey { Name = "test", Key = ApiKey }],
            },
            Logging = new LoggingSettings { Directory = Path.Combine(_tempDir, "logs") },
            Dashboard = new DashboardSettings { Enabled = true, Port = ApiPort },
        };

        var signingKeyManager = new SigningKeyManager(Config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(Config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger();

        // Initialize SQLite for SettingsStore
        var dbPath = Path.Combine(_tempDir, "test.db");
        var dbOptions = new DbContextOptionsBuilder<GateSqlDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        await using (var initDb = new GateSqlDbContext(dbOptions))
            await initDb.Database.EnsureCreatedAsync();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{ApiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddDbContextFactory<GateSqlDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddSingleton(Config);
        builder.Services.AddSingleton(_jwtAuth);
        builder.Services.AddSingleton(_sessionManager);
        builder.Services.AddSingleton(_queryLogger);
        builder.Services.AddSingleton(new DbProxy.Query.SchemaIntrospector(Config));
        builder.Services.AddSingleton(new SetupState { SetupRequired = false });

        // Build a temporary ServiceProvider to create SettingsStore
        var tempSp = builder.Services.BuildServiceProvider();
        var dbFactory = tempSp.GetRequiredService<IDbContextFactory<GateSqlDbContext>>();
        var settingsStore = new SettingsStore(dbFactory);
        await settingsStore.InitializeAsync();
        builder.Services.AddSingleton(settingsStore);

        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(DashboardController).Assembly)
            .AddRazorOptions(options =>
            {
                options.ViewLocationFormats.Clear();
                options.ViewLocationFormats.Add("/Dashboard/Views/{1}/{0}.cshtml");
                options.ViewLocationFormats.Add("/Dashboard/Views/Shared/{0}.cshtml");
            });

        _webApp = builder.Build();

        _webApp.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(
                Path.Combine(AppContext.BaseDirectory, "Dashboard", "wwwroot")),
        });

        DbProxy.Api.AdminApiEndpoints.MapAdminApi(_webApp, Config, _jwtAuth, _sessionManager, _queryLogger);
        _webApp.MapControllerRoute(name: "default", pattern: "{controller=Dashboard}/{action=Index}/{id?}");

        _cts = new CancellationTokenSource();

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _pgHandler = new PgProtocolHandler(Config, _jwtAuth, _sessionManager, _queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());

        _ = Task.Run(() => _pgHandler.StartAsync(_cts.Token));
        _ = Task.Run(() => _webApp.RunAsync(_cts.Token));

        await Task.Delay(500);
    }

    public HttpClient CreateAuthenticatedClient()
    {
        var cookies = new CookieContainer();
        cookies.Add(new Uri($"http://127.0.0.1:{ApiPort}"), new Cookie("gatesql_key", ApiKey));
        var handler = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };
        return new HttpClient(handler) { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}") };
    }

    public string BuildConnectionString(string token)
    {
        return $"Host=127.0.0.1;Port={ProxyPort};Database=postgres;Username=agent;Password={token}";
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _pgHandler.Dispose();
        _sessionManager.Dispose();
        _queryLogger.Dispose();
        await _webApp.DisposeAsync();
        await _pg.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private static int GetFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        int port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
