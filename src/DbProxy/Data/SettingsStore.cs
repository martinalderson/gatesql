using DbProxy.Configuration;
using Microsoft.EntityFrameworkCore;

namespace DbProxy.Data;

public class SettingsStore
{
    private readonly IDbContextFactory<GateSqlDbContext> _dbFactory;
    private Dictionary<string, string> _cache = new();

    public SettingsStore(IDbContextFactory<GateSqlDbContext> dbFactory)
    {
        _dbFactory = dbFactory;
    }

    /// <summary>Creates the Settings table (if missing) and loads all values into memory.</summary>
    public async Task InitializeAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        await db.Database.ExecuteSqlRawAsync(
            "CREATE TABLE IF NOT EXISTS \"Settings\" (\"Key\" TEXT PRIMARY KEY, \"Value\" TEXT NOT NULL)");

        _cache = await db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value);
    }

    public string? Get(string key) => _cache.GetValueOrDefault(key);

    public async Task SetAsync(string key, string value)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Settings.FindAsync(key);
        if (entity != null)
            entity.Value = value;
        else
            db.Settings.Add(new SettingsEntity { Key = key, Value = value });
        await db.SaveChangesAsync();
        _cache[key] = value;
    }

    public string? GetApiKey() => Get("auth.apiKey");

    public async Task SaveApiKey(string apiKey) => await SetAsync("auth.apiKey", apiKey);

    public bool HasUpstreamConfig() => Get("upstream.host") != null;

    public void ApplyUpstreamToConfig(ProxyConfig config)
    {
        var host = Get("upstream.host");
        if (host == null) return;

        config.Upstream.Host = host;

        if (Get("upstream.port") is { } port && int.TryParse(port, out var p))
            config.Upstream.Port = p;
        if (Get("upstream.database") is { } database)
            config.Upstream.Database = database;
        if (Get("upstream.username") is { } username)
            config.Upstream.Username = username;
        if (Get("upstream.password") is { } password)
            config.Upstream.Password = password;
        if (Get("upstream.sslMode") is { } sslMode
            && Enum.TryParse<UpstreamSslMode>(sslMode, ignoreCase: true, out var mode))
            config.Upstream.SslMode = mode;
    }

    public void ApplyApiKeyToConfig(ProxyConfig config)
    {
        var apiKey = GetApiKey();
        if (apiKey != null && config.Auth.ParentApiKeys.Count == 0)
            config.Auth.ParentApiKeys = [new ParentApiKey { Name = "auto-generated", Key = apiKey }];
    }

    public async Task SaveUpstreamConfig(UpstreamSettings upstream)
    {
        await SetAsync("upstream.host", upstream.Host);
        await SetAsync("upstream.port", upstream.Port.ToString());
        await SetAsync("upstream.database", upstream.Database);
        await SetAsync("upstream.username", upstream.Username);
        await SetAsync("upstream.password", upstream.Password);
        await SetAsync("upstream.sslMode", upstream.SslMode.ToString());
    }
}
