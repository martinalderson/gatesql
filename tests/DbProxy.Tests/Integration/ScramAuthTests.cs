using System.Net.Http.Json;
using System.Text.Json;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class ScramAuthTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17")
        .WithUsername("scramuser")
        .WithPassword("scrampass")
        .WithCommand("-c", "password_encryption=scram-sha-256")
        .Build();

    private PgProtocolHandler _handler = null!;
    private WebApplication _webApp = null!;
    private JwtAuthenticator _jwtAuth = null!;
    private SessionManager _sessionManager = null!;
    private QueryLogger _queryLogger = null!;
    private CancellationTokenSource _cts = null!;
    private string _tempDir = null!;
    private int _proxyPort;
    private int _apiPort;
    private const string ApiKey = "test_scram_key";

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();

        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-scram-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _proxyPort = GetFreePort();
        _apiPort = GetFreePort();

        var config = new ProxyConfig
        {
            Proxy = new ProxySettings { ListenPort = _proxyPort, ListenHost = "127.0.0.1" },
            Upstream = new UpstreamSettings
            {
                Host = _pg.Hostname,
                Port = _pg.GetMappedPublicPort(5432),
                Database = "postgres",
                Username = "scramuser",
                Password = "scrampass",
            },
            Auth = new AuthSettings
            {
                HardCapMinutes = 60,
                IdleTimeoutMinutes = 5,
                SigningKeyPath = Path.Combine(_tempDir, "signing.key"),
                ParentApiKeys = [new ParentApiKey { Name = "test", Key = ApiKey }],
            },
            Logging = new LoggingSettings { Directory = Path.Combine(_tempDir, "logs") },
            Dashboard = new DashboardSettings { Enabled = false, Port = _apiPort },
        };

        var signingKeyManager = new SigningKeyManager(config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger(config.Logging.Directory);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{_apiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        _webApp = builder.Build();
        DbProxy.Api.AdminApiEndpoints.MapAdminApi(_webApp, config, _jwtAuth, _sessionManager, _queryLogger);

        _cts = new CancellationTokenSource();
        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _handler = new PgProtocolHandler(config, _jwtAuth, _sessionManager, _queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());

        _ = Task.Run(() => _handler.StartAsync(_cts.Token));
        _ = Task.Run(() => _webApp.RunAsync(_cts.Token));
        await Task.Delay(500);
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        _handler.Dispose();
        _sessionManager.Dispose();
        _queryLogger.Dispose();
        await _webApp.DisposeAsync();
        await _pg.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task Connect_ViaScramSha256_CanRunQuery()
    {
        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "/* <agent_purpose>scram auth test</agent_purpose> */ SELECT 1 AS result", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Connect_ViaScramSha256_MultipleQueries()
    {
        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        for (int i = 1; i <= 5; i++)
        {
            await using var cmd = new NpgsqlCommand(
                $"/* <agent_purpose>scram multi-query {i}</agent_purpose> */ SELECT {i}", conn);
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(i, result);
        }
    }

    private async Task<(string token, string sessionId)> CreateSessionAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{_apiPort}/api/sessions",
            new { agentId = "scram-agent", task = "scram-test" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("sessionId").GetString()!);
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
