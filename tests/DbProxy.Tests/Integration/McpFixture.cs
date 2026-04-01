using System.Net.Http.Json;
using System.Text.Json;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Data;
using DbProxy.Mcp;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class McpFixture : IAsyncLifetime
{
    public ProxyConfig Config { get; private set; } = null!;
    public int ApiPort { get; private set; }
    public string ApiKey => "test_key_123";

    private PostgreSqlContainer _pg = null!;
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

        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-mcp-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        ApiPort = GetFreePort();

        Config = new ProxyConfig
        {
            Proxy = new ProxySettings { ListenPort = GetFreePort(), ListenHost = "0.0.0.0" },
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
            Mcp = new McpSettings { Enabled = true },
        };

        var signingKeyManager = new SigningKeyManager(Config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(Config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger();

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{ApiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        var dbPath = Path.Combine(_tempDir, "test.db");
        builder.Services.AddDbContextFactory<GateSqlDbContext>(options =>
            options.UseSqlite($"Data Source={dbPath}"));

        builder.Services.AddSingleton(Config);
        builder.Services.AddSingleton(_jwtAuth);
        builder.Services.AddSingleton(_sessionManager);
        builder.Services.AddSingleton(_queryLogger);
        builder.Services.AddSingleton(new SchemaIntrospector(Config));
        builder.Services.AddSingleton<GovernedQueryExecutor>();
        builder.Services.AddSingleton<McpSessionContext>();

        builder.Services.AddMcpServer()
            .WithHttpTransport()
            .WithTools<McpTools>();

        _webApp = builder.Build();

        var dbFactory = _webApp.Services.GetRequiredService<IDbContextFactory<GateSqlDbContext>>();
        using (var db = dbFactory.CreateDbContext())
            db.Database.EnsureCreated();

        DbProxy.Api.AdminApiEndpoints.MapAdminApi(_webApp, Config, _jwtAuth, _sessionManager, _queryLogger,
            new SchemaIntrospector(Config, dbFactory));
        _webApp.MapMcpServer(Config, _jwtAuth, _sessionManager);

        _cts = new CancellationTokenSource();
        _ = Task.Run(() => _webApp.RunAsync(_cts.Token));

        await Task.Delay(500);

        // Seed test data
        await SeedDatabaseAsync();
    }

    private async Task SeedDatabaseAsync()
    {
        var connStr = $"Host={_pg.Hostname};Port={_pg.GetMappedPublicPort(5432)};" +
                      "Database=postgres;Username=testuser;Password=testpass";
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new Npgsql.NpgsqlCommand("""
            CREATE TABLE IF NOT EXISTS orders (
                id SERIAL PRIMARY KEY,
                customer TEXT NOT NULL,
                total NUMERIC(10,2) NOT NULL,
                created_at TIMESTAMP DEFAULT NOW()
            );
            INSERT INTO orders (customer, total) VALUES ('Alice', 100.00), ('Bob', 200.00), ('Charlie', 50.00);

            CREATE TABLE IF NOT EXISTS products (
                id SERIAL PRIMARY KEY,
                name TEXT NOT NULL,
                price NUMERIC(10,2) NOT NULL
            );
            INSERT INTO products (name, price) VALUES ('Widget', 9.99), ('Gadget', 19.99);
            """, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<(string token, string sessionId)> CreateSessionAsync(
        string agentId = "test-agent", string task = "test-task",
        int? queryBudget = null, bool readOnly = false,
        string dangerousQueryMode = "block", List<string>? allowedTables = null)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);

        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{ApiPort}/api/sessions",
            new { agentId, task, queryBudget, readOnly, dangerousQueryMode, allowedTables });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("sessionId").GetString()!);
    }

    public HttpClient CreateMcpHttpClient(string token)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{ApiPort}") };
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
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
