using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Protocol;
using DbProxy.Query;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Npgsql;
using Testcontainers.PostgreSql;

namespace DbProxy.Tests.Integration;

public class UpstreamSslTests : IAsyncLifetime
{
    private IContainer _pg = null!;
    private PgProtocolHandler _handler = null!;
    private WebApplication _webApp = null!;
    private JwtAuthenticator _jwtAuth = null!;
    private SessionManager _sessionManager = null!;
    private QueryLogger _queryLogger = null!;
    private CancellationTokenSource _cts = null!;
    private string _tempDir = null!;
    private string _caCertPath = null!;
    private int _proxyPort;
    private int _apiPort;
    private int _pgPort;
    private const string ApiKey = "test_ssl_key";

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"dbproxy-ssl-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var certDir = Path.Combine(_tempDir, "certs");
        Directory.CreateDirectory(certDir);

        // Generate self-signed CA + server cert
        var (caCertPem, serverCertPem, serverKeyPem) = GenerateTestCertificates("localhost");
        _caCertPath = Path.Combine(certDir, "ca.crt");
        File.WriteAllText(_caCertPath, caCertPem);
        File.WriteAllText(Path.Combine(certDir, "server.crt"), serverCertPem);
        File.WriteAllText(Path.Combine(certDir, "server.key"), serverKeyPem);

        _pg = new ContainerBuilder("postgres:17")
            .WithEnvironment("POSTGRES_USER", "ssluser")
            .WithEnvironment("POSTGRES_PASSWORD", "sslpass")
            .WithEnvironment("POSTGRES_DB", "postgres")
            .WithPortBinding(5432, true)
            .WithResourceMapping(certDir, "/certs-src")
            .WithEntrypoint("/bin/bash")
            .WithCommand("-c",
                "mkdir -p /tmp/pg-certs && " +
                "cp /certs-src/* /tmp/pg-certs/ && " +
                "chown postgres:postgres /tmp/pg-certs/* && " +
                "chmod 600 /tmp/pg-certs/server.key && " +
                "exec docker-entrypoint.sh postgres " +
                "-c ssl=on " +
                "-c ssl_cert_file=/tmp/pg-certs/server.crt " +
                "-c ssl_key_file=/tmp/pg-certs/server.key " +
                "-c ssl_ca_file=/tmp/pg-certs/ca.crt")
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilMessageIsLogged("database system is ready to accept connections"))
            .Build();

        await _pg.StartAsync();

        _pgPort = _pg.GetMappedPublicPort(5432);
    }

    private void StartProxy(UpstreamSslMode sslMode, string? caCertPath = null, int? pgPort = null)
    {
        _proxyPort = GetFreePort();
        _apiPort = GetFreePort();

        var config = new ProxyConfig
        {
            Proxy = new ProxySettings { ListenPort = _proxyPort, ListenHost = "127.0.0.1" },
            Upstream = new UpstreamSettings
            {
                Host = "127.0.0.1",
                Port = pgPort ?? _pgPort,
                Database = "postgres",
                Username = "ssluser",
                Password = "sslpass",
                SslMode = sslMode,
                SslCaCertPath = caCertPath,
            },
            Auth = new AuthSettings
            {
                HardCapMinutes = 60,
                IdleTimeoutMinutes = 5,
                SigningKeyPath = Path.Combine(_tempDir, $"signing-{Guid.NewGuid():N}.key"),
                ParentApiKeys = [new ParentApiKey { Name = "test", Key = ApiKey }],
            },
            Logging = new LoggingSettings { Directory = Path.Combine(_tempDir, "logs") },
            Dashboard = new DashboardSettings { Enabled = false, Port = _apiPort },
        };

        var signingKeyManager = new SigningKeyManager(config.Auth.SigningKeyPath);
        _jwtAuth = new JwtAuthenticator(signingKeyManager);
        _sessionManager = new SessionManager(TimeSpan.FromMinutes(config.Auth.IdleTimeoutMinutes));
        _queryLogger = new QueryLogger();

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
    }

    public async Task DisposeAsync()
    {
        if (_cts != null)
        {
            _cts.Cancel();
            _handler?.Dispose();
            _sessionManager?.Dispose();
            _queryLogger?.Dispose();
            if (_webApp != null) await _webApp.DisposeAsync();
        }
        await _pg.DisposeAsync();
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Fact]
    public async Task UpstreamSsl_Require_ConnectsAndQueries()
    {
        StartProxy(UpstreamSslMode.Require);
        await Task.Delay(500);

        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "/* <agent_purpose>ssl require test</agent_purpose> */ SELECT 1 AS result", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task UpstreamSsl_VerifyCa_WithValidCaCert_Succeeds()
    {
        StartProxy(UpstreamSslMode.VerifyCa, _caCertPath);
        await Task.Delay(500);

        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "/* <agent_purpose>ssl verify-ca test</agent_purpose> */ SELECT 42", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task UpstreamSsl_VerifyFull_WithValidCaCert_Succeeds()
    {
        StartProxy(UpstreamSslMode.VerifyFull, _caCertPath);
        await Task.Delay(500);

        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand(
            "/* <agent_purpose>ssl verify-full test</agent_purpose> */ SELECT 99", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(99, result);
    }

    [Fact]
    public async Task UpstreamSsl_VerifyCa_WithWrongCaCert_Fails()
    {
        // Generate a different CA cert
        var wrongCertDir = Path.Combine(_tempDir, "wrong-certs");
        Directory.CreateDirectory(wrongCertDir);
        var (wrongCaPem, _, _) = GenerateTestCertificates("wrong-ca");
        var wrongCaPath = Path.Combine(wrongCertDir, "wrong-ca.crt");
        File.WriteAllText(wrongCaPath, wrongCaPem);

        StartProxy(UpstreamSslMode.VerifyCa, wrongCaPath);
        await Task.Delay(500);

        var (token, _) = await CreateSessionAsync();
        var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token};Timeout=5";

        await using var conn = new NpgsqlConnection(connStr);
        await Assert.ThrowsAnyAsync<NpgsqlException>(() => conn.OpenAsync());
    }

    [Fact]
    public async Task UpstreamSsl_Prefer_WithNonSslServer_FallsBack()
    {
        // Start a plain PostgreSQL container (no SSL)
        var plainPg = new PostgreSqlBuilder("postgres:17")
            .WithUsername("plainuser")
            .WithPassword("plainpass")
            .Build();
        await plainPg.StartAsync();

        try
        {
            var plainPort = plainPg.GetMappedPublicPort(5432);

            _proxyPort = GetFreePort();
            _apiPort = GetFreePort();

            var config = new ProxyConfig
            {
                Proxy = new ProxySettings { ListenPort = _proxyPort, ListenHost = "127.0.0.1" },
                Upstream = new UpstreamSettings
                {
                    Host = "127.0.0.1",
                    Port = plainPort,
                    Database = "postgres",
                    Username = "plainuser",
                    Password = "plainpass",
                    SslMode = UpstreamSslMode.Prefer,
                },
                Auth = new AuthSettings
                {
                    HardCapMinutes = 60,
                    IdleTimeoutMinutes = 5,
                    SigningKeyPath = Path.Combine(_tempDir, $"signing-prefer-{Guid.NewGuid():N}.key"),
                    ParentApiKeys = [new ParentApiKey { Name = "test", Key = ApiKey }],
                },
                Logging = new LoggingSettings { Directory = Path.Combine(_tempDir, "logs") },
                Dashboard = new DashboardSettings { Enabled = false, Port = _apiPort },
            };

            var signingKeyManager = new SigningKeyManager(config.Auth.SigningKeyPath);
            _jwtAuth = new JwtAuthenticator(signingKeyManager);
            _sessionManager = new SessionManager(TimeSpan.FromMinutes(config.Auth.IdleTimeoutMinutes));
            _queryLogger = new QueryLogger();

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

            var (token, _) = await CreateSessionAsync();
            var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token}";

            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "/* <agent_purpose>ssl prefer fallback test</agent_purpose> */ SELECT 7", conn);
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(7, result);
        }
        finally
        {
            await plainPg.DisposeAsync();
        }
    }

    [Fact]
    public async Task UpstreamSsl_Require_WithNonSslServer_Fails()
    {
        var plainPg = new PostgreSqlBuilder("postgres:17")
            .WithUsername("plainuser")
            .WithPassword("plainpass")
            .Build();
        await plainPg.StartAsync();

        try
        {
            var plainPort = plainPg.GetMappedPublicPort(5432);
            StartProxy(UpstreamSslMode.Require, pgPort: plainPort);
            // Override upstream to point at plain PG
            await Task.Delay(500);

            var (token, _) = await CreateSessionAsync();
            var connStr = $"Host=127.0.0.1;Port={_proxyPort};Database=postgres;Username=agent;Password={token};Timeout=5";

            await using var conn = new NpgsqlConnection(connStr);
            await Assert.ThrowsAnyAsync<NpgsqlException>(() => conn.OpenAsync());
        }
        finally
        {
            await plainPg.DisposeAsync();
        }
    }

    private async Task<(string token, string sessionId)> CreateSessionAsync()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", ApiKey);
        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{_apiPort}/api/sessions",
            new { agentId = "ssl-agent", task = "ssl-test" });
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("sessionId").GetString()!);
    }

    private static (string caCertPem, string serverCertPem, string serverKeyPem) GenerateTestCertificates(string hostname)
    {
        using var caKey = RSA.Create(2048);
        var caReq = new CertificateRequest("CN=Test CA", caKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        caReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        using var caCert = caReq.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));

        using var serverKey = RSA.Create(2048);
        var serverReq = new CertificateRequest($"CN={hostname}", serverKey, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        serverReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));

        var sanBuilder = new SubjectAlternativeNameBuilder();
        sanBuilder.AddDnsName(hostname);
        sanBuilder.AddDnsName("localhost");
        sanBuilder.AddIpAddress(System.Net.IPAddress.Loopback);
        serverReq.CertificateExtensions.Add(sanBuilder.Build());

        var serialNumber = new byte[8];
        RandomNumberGenerator.Fill(serialNumber);
        using var serverCert = serverReq.Create(caCert, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1), serialNumber);

        var caCertPem = caCert.ExportCertificatePem();
        var serverCertPem = serverCert.ExportCertificatePem();
        var serverKeyPem = serverKey.ExportRSAPrivateKeyPem();

        return (caCertPem, serverCertPem, serverKeyPem);
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
