using System.Net.Http.Json;
using System.Text.Json;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Data;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class ProxyFixture : IAsyncLifetime
{
    private readonly string _pgImage;

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

    public ProxyFixture(string pgImage = "postgres:17")
    {
        _pgImage = pgImage;
    }

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder(_pgImage)
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
        await _pg.StartAsync();

        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-test-{Guid.NewGuid():N}");
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
            Dashboard = new DashboardSettings { Enabled = false, Port = ApiPort },
        };

        var signingKeyManager = new SigningKeyManager(Config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(Config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{ApiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // SQLite for schema annotations
        var dbPath = Path.Combine(_tempDir, "test.db");
        builder.Services.AddDbContextFactory<GateSqlDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        _webApp = builder.Build();

        // Initialize DB
        var dbFactory = _webApp.Services.GetRequiredService<IDbContextFactory<GateSqlDbContext>>();
        using (var db = dbFactory.CreateDbContext())
            db.Database.EnsureCreated();

        var schemaIntrospector = new SchemaIntrospector(Config, dbFactory);
        DbProxy.Api.AdminApiEndpoints.MapAdminApi(_webApp, Config, _jwtAuth, _sessionManager, _queryLogger, schemaIntrospector);

        _cts = new CancellationTokenSource();

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _pgHandler = new PgProtocolHandler(Config, _jwtAuth, _sessionManager, _queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());

        _ = Task.Run(() => _pgHandler.StartAsync(_cts.Token));
        _ = Task.Run(() => _webApp.RunAsync(_cts.Token));

        await Task.Delay(500);
    }

    public async Task<(string token, string sessionId)> CreateSessionAsync(
        string agentId = "test-agent",
        string task = "test-task",
        int? queryBudget = null)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{ApiPort}/api/sessions",
            new { agentId, task, queryBudget });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("sessionId").GetString()!);
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
