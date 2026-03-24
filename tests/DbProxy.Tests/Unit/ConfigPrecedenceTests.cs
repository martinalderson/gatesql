using DbProxy.Configuration;
using DbProxy.Data;
using Microsoft.EntityFrameworkCore;

namespace DbProxy.Tests.Unit;

public class ConfigPrecedenceTests : IAsyncLifetime
{
    private Microsoft.Data.Sqlite.SqliteConnection _connection = null!;
    private IDbContextFactory<GateSqlDbContext> _dbFactory = null!;

    public async Task InitializeAsync()
    {
        _connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await _connection.OpenAsync();

        _dbFactory = new InMemoryDbContextFactory(_connection);

        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.EnsureCreatedAsync();
    }

    public Task DisposeAsync()
    {
        _connection.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public void Defaults_AppliedWhenNoConfig()
    {
        var config = new ProxyConfig();

        Assert.Equal("localhost", config.Upstream.Host);
        Assert.Equal(5433, config.Upstream.Port);
        Assert.Equal("postgres", config.Upstream.Database);
        Assert.Empty(config.Auth.ParentApiKeys);
    }

    [Fact]
    public async Task SqliteSettings_OverrideDefaults()
    {
        var store = new SettingsStore(_dbFactory);
        await store.InitializeAsync();

        await store.SaveUpstreamConfig(new UpstreamSettings
        {
            Host = "sqlite-host",
            Port = 9999,
            Database = "sqlite-db",
            Username = "sqlite-user",
            Password = "sqlite-pass",
        });

        var config = new ProxyConfig();
        store.ApplyUpstreamToConfig(config);

        Assert.Equal("sqlite-host", config.Upstream.Host);
        Assert.Equal(9999, config.Upstream.Port);
        Assert.Equal("sqlite-db", config.Upstream.Database);
    }

    [Fact]
    public async Task SqliteApiKey_OnlyAppliesWhenNoExistingKeys()
    {
        var store = new SettingsStore(_dbFactory);
        await store.InitializeAsync();
        await store.SaveApiKey("gatesql_pk_from_sqlite");

        // Config with no keys — SQLite should apply
        var config1 = new ProxyConfig();
        store.ApplyApiKeyToConfig(config1);
        Assert.Equal("gatesql_pk_from_sqlite", config1.Auth.ParentApiKeys[0].Key);

        // Config with existing keys (from config.json or env) — SQLite should NOT override
        var config2 = new ProxyConfig
        {
            Auth = new AuthSettings
            {
                ParentApiKeys = [new ParentApiKey { Name = "from-config", Key = "pk_from_config" }]
            }
        };
        store.ApplyApiKeyToConfig(config2);
        Assert.Equal("pk_from_config", config2.Auth.ParentApiKeys[0].Key);
    }

    [Fact]
    public async Task FullPrecedence_ConfigJsonThenEnvThenSqlite()
    {
        // Simulate: config.json sets host to "config-host"
        var config = new ProxyConfig();
        config.Upstream.Host = "config-host";
        config.Upstream.Port = 1111;

        // Simulate: env var overrides port
        config.Upstream.Port = 2222;

        // SQLite overrides host (wizard was used)
        var store = new SettingsStore(_dbFactory);
        await store.InitializeAsync();
        await store.SaveUpstreamConfig(new UpstreamSettings
        {
            Host = "wizard-host",
            Port = 3333,
            Database = "wizard-db",
            Username = "wizard-user",
            Password = "wizard-pass",
        });
        store.ApplyUpstreamToConfig(config);

        // SQLite wins (highest priority)
        Assert.Equal("wizard-host", config.Upstream.Host);
        Assert.Equal(3333, config.Upstream.Port);
        Assert.Equal("wizard-db", config.Upstream.Database);
    }

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

        public Task<GateSqlDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(CreateDbContext());
        }
    }
}
