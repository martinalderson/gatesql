using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Protocol;
using DbProxy.Query;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class GracefulShutdownTests : IAsyncLifetime
{
    private PostgreSqlContainer _pg = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _pg = new PostgreSqlBuilder("postgres:17")
            .WithUsername("testuser")
            .WithPassword("testpass")
            .Build();
        await _pg.StartAsync();
        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-shutdown-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public async Task DisposeAsync()
    {
        await _pg.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task Shutdown_WithNoConnections_ExitsQuickly()
    {
        var (handler, cts) = CreateHandler();
        var proxyTask = Task.Run(() => handler.StartAsync(cts.Token));
        await Task.Delay(300);

        cts.Cancel();
        var completed = await Task.WhenAny(proxyTask, Task.Delay(3000));

        Assert.Equal(proxyTask, completed);
        handler.Dispose();
    }

    [Fact]
    public async Task Shutdown_WithActiveConnection_WaitsAndExits()
    {
        var config = CreateConfig();
        var signingKeyManager = new SigningKeyManager(config.Auth.SigningKeyPath);
        var jwtAuth = new JwtAuthenticator(signingKeyManager);
        var sessionManager = new SessionManager(TimeSpan.FromMinutes(5));
        var queryLogger = new QueryLogger();
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        var handler = new PgProtocolHandler(config, jwtAuth, sessionManager, queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());
        var cts = new CancellationTokenSource();

        var proxyTask = Task.Run(() => handler.StartAsync(cts.Token));
        await Task.Delay(500);

        // Create a session and connect
        var sessionId = $"sess_{Guid.NewGuid():N}";
        sessionManager.CreateSession(sessionId, "test", "shutdown-test", null, DateTime.UtcNow.AddHours(1));
        var token = jwtAuth.GenerateToken(sessionId, "test", "shutdown-test", null, TimeSpan.FromHours(1));
        var connStr = $"Host=127.0.0.1;Port={config.Proxy.ListenPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Cancel while connection is active
        cts.Cancel();
        var completed = await Task.WhenAny(proxyTask, Task.Delay(5000));

        Assert.Equal(proxyTask, completed);
        handler.Dispose();
        sessionManager.Dispose();
        queryLogger.Dispose();
    }

    private (PgProtocolHandler handler, CancellationTokenSource cts) CreateHandler()
    {
        var config = CreateConfig();
        var signingKeyManager = new SigningKeyManager(Path.Combine(_tempDir, "signing.key"));
        var jwtAuth = new JwtAuthenticator(signingKeyManager);
        var sessionManager = new SessionManager(TimeSpan.FromMinutes(5));
        var queryLogger = new QueryLogger();
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));

        var handler = new PgProtocolHandler(config, jwtAuth, sessionManager, queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());

        return (handler, new CancellationTokenSource());
    }

    private ProxyConfig CreateConfig()
    {
        var port = GetFreePort();
        return new ProxyConfig
        {
            Proxy = new ProxySettings { ListenPort = port, ListenHost = "0.0.0.0" },
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
                ParentApiKeys = [new ParentApiKey { Name = "test", Key = "test_key" }],
            },
            Logging = new LoggingSettings { Directory = Path.Combine(_tempDir, "logs") },
            Dashboard = new DashboardSettings { Enabled = false, Port = GetFreePort() },
        };
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
