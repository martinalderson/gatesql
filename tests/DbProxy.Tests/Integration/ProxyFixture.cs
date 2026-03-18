using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Protocol;
using DbProxy.Query;
using Microsoft.Extensions.Logging;

namespace DbProxy.Tests.Integration;

public class ProxyFixture : IAsyncLifetime
{
    public ProxyConfig Config { get; private set; } = null!;
    public int ProxyPort { get; private set; }
    public int ApiPort { get; private set; }
    public string ApiKey => "test_key_123";

    private PgProtocolHandler _pgHandler = null!;
    private WebApplication _webApp = null!;
    private JwtAuthenticator _jwtAuth = null!;
    private SessionManager _sessionManager = null!;
    private QueryLogger _queryLogger = null!;
    private CancellationTokenSource _cts = null!;
    private string _tempDir = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // Find free ports
        ProxyPort = GetFreePort();
        ApiPort = GetFreePort();

        Config = new ProxyConfig
        {
            Proxy = new ProxySettings
            {
                ListenPort = ProxyPort,
                ListenHost = "127.0.0.1",
            },
            Upstream = new UpstreamSettings
            {
                Host = "127.0.0.1",
                Port = 5432,
                Database = "postgres",
                Username = "postgres",
                Password = "postgres",
            },
            Auth = new AuthSettings
            {
                HardCapMinutes = 60,
                IdleTimeoutMinutes = 5,
                SigningKeyPath = Path.Combine(_tempDir, "signing.key"),
                ParentApiKeys = [new ParentApiKey { Name = "test", Key = ApiKey }],
            },
            Logging = new LoggingSettings
            {
                Directory = Path.Combine(_tempDir, "logs"),
            },
            Dashboard = new DashboardSettings
            {
                Enabled = false,
                Port = ApiPort,
            },
        };

        var signingKeyManager = new SigningKeyManager(Config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(Config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger(Config.Logging.Directory);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls($"http://127.0.0.1:{ApiPort}");
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        _webApp = builder.Build();
        DbProxy.Api.AdminApiEndpoints.MapAdminApi(_webApp, Config, _jwtAuth, _sessionManager, _queryLogger);

        _cts = new CancellationTokenSource();

        var loggerFactory = LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug));
        _pgHandler = new PgProtocolHandler(Config, _jwtAuth, _sessionManager, _queryLogger,
            loggerFactory.CreateLogger<PgProtocolHandler>());

        _ = Task.Run(() => _pgHandler.StartAsync(_cts.Token));
        _ = Task.Run(() => _webApp.RunAsync(_cts.Token));

        // Wait for proxy to be ready
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
