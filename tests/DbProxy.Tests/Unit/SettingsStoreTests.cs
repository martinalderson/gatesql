using DbProxy.Configuration;
using DbProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace DbProxy.Tests.Unit;

public class SettingsStoreTests : IAsyncLifetime
{
    private IDbContextFactory<GateSqlDbContext> _dbFactory = null!;
    private SettingsStore _store = null!;

    public async Task InitializeAsync()
    {
        var options = new DbContextOptionsBuilder<GateSqlDbContext>()
            .UseSqlite("Data Source=:memory:")
            .Options;

        // In-memory SQLite needs a persistent connection
        var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var factory = new InMemoryDbContextFactory(connection);
        _dbFactory = factory;

        // Create full schema
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();

        _store = new SettingsStore(_dbFactory);
        await _store.InitializeAsync();
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task SetAndGet_RoundTrips()
    {
        await _store.SetAsync("test.key", "test-value");
        Assert.Equal("test-value", _store.Get("test.key"));
    }

    [Fact]
    public void Get_MissingKey_ReturnsNull()
    {
        Assert.Null(_store.Get("nonexistent"));
    }

    [Fact]
    public async Task SaveApiKey_PersistsAndRetrieves()
    {
        await _store.SaveApiKey("gatesql_pk_abc123");
        Assert.Equal("gatesql_pk_abc123", _store.GetApiKey());
    }

    [Fact]
    public async Task HasUpstreamConfig_FalseByDefault()
    {
        Assert.False(_store.HasUpstreamConfig());

        await _store.SaveUpstreamConfig(new UpstreamSettings { Host = "db.example.com", Port = 5432 });
        Assert.True(_store.HasUpstreamConfig());
    }

    [Fact]
    public async Task SaveUpstreamConfig_AppliesAllFields()
    {
        var upstream = new UpstreamSettings
        {
            Host = "db.example.com",
            Port = 5433,
            Database = "myapp",
            Username = "admin",
            Password = "secret",
            SslMode = UpstreamSslMode.Require,
        };
        await _store.SaveUpstreamConfig(upstream);

        var config = new ProxyConfig();
        _store.ApplyUpstreamToConfig(config);

        Assert.Equal("db.example.com", config.Upstream.Host);
        Assert.Equal(5433, config.Upstream.Port);
        Assert.Equal("myapp", config.Upstream.Database);
        Assert.Equal("admin", config.Upstream.Username);
        Assert.Equal("secret", config.Upstream.Password);
        Assert.Equal(UpstreamSslMode.Require, config.Upstream.SslMode);
    }

    [Fact]
    public async Task ApplyApiKeyToConfig_OnlyWhenNoKeysExist()
    {
        await _store.SaveApiKey("gatesql_pk_saved");

        // Should apply when config has no keys
        var config = new ProxyConfig();
        _store.ApplyApiKeyToConfig(config);
        Assert.Single(config.Auth.ParentApiKeys);
        Assert.Equal("gatesql_pk_saved", config.Auth.ParentApiKeys[0].Key);

        // Should NOT override when config already has keys
        var configWithKeys = new ProxyConfig
        {
            Auth = new AuthSettings
            {
                ParentApiKeys = [new ParentApiKey { Name = "existing", Key = "pk_existing" }]
            }
        };
        _store.ApplyApiKeyToConfig(configWithKeys);
        Assert.Single(configWithKeys.Auth.ParentApiKeys);
        Assert.Equal("pk_existing", configWithKeys.Auth.ParentApiKeys[0].Key);
    }

    [Fact]
    public async Task ApplyUpstreamToConfig_NoopWhenNoSettings()
    {
        var config = new ProxyConfig();
        var originalHost = config.Upstream.Host;

        _store.ApplyUpstreamToConfig(config);

        Assert.Equal(originalHost, config.Upstream.Host);
    }

    [Fact]
    public async Task SetAsync_OverwritesExistingValue()
    {
        await _store.SetAsync("key", "value1");
        Assert.Equal("value1", _store.Get("key"));

        await _store.SetAsync("key", "value2");
        Assert.Equal("value2", _store.Get("key"));
    }

    /// <summary>In-memory SQLite factory sharing a single persistent connection.</summary>
    private class InMemoryDbContextFactory : IDbContextFactory<GateSqlDbContext>
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;

        public InMemoryDbContextFactory(Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            _connection = connection;
        }

        public GateSqlDbContext CreateDbContext()
        {
            var options = new DbContextOptionsBuilder<GateSqlDbContext>()
                .UseSqlite(_connection)
                .Options;
            return new GateSqlDbContext(options);
        }

        public async Task<GateSqlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return CreateDbContext();
        }
    }
}
