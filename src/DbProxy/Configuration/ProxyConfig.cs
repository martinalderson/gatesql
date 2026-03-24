using System.Text.Json.Serialization;

namespace DbProxy.Configuration;

[JsonConverter(typeof(JsonStringEnumConverter<UpstreamSslMode>))]
public enum UpstreamSslMode
{
    Disable,
    Prefer,
    Require,
    VerifyCa,
    VerifyFull,
}

public class ProxyConfig
{
    public ProxySettings Proxy { get; set; } = new();
    public UpstreamSettings Upstream { get; set; } = new();
    public AuthSettings Auth { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();
    public DashboardSettings Dashboard { get; set; } = new();
    public StorageSettings Storage { get; set; } = new();
}

public class ProxySettings
{
    public int ListenPort { get; set; } = 5432;
    public string ListenHost { get; set; } = "0.0.0.0";
    public string? TlsCertPath { get; set; }
    public string? TlsKeyPath { get; set; }
}

public class UpstreamSettings
{
    public string Host { get; set; } = "localhost";
    public int Port { get; set; } = 5433;
    public string Database { get; set; } = "postgres";
    public string Username { get; set; } = "postgres";
    public string Password { get; set; } = "";
    public int MaxConnections { get; set; } = 20;
    public UpstreamSslMode SslMode { get; set; } = UpstreamSslMode.Disable;
    public string? SslCaCertPath { get; set; }
    public string? SslClientCertPath { get; set; }
    public string? SslClientKeyPath { get; set; }
}

public class AuthSettings
{
    public int HardCapMinutes { get; set; } = 480;
    public int IdleTimeoutMinutes { get; set; } = 15;
    public string SigningKeyPath { get; set; } = "keys/signing.key";
    public List<ParentApiKey> ParentApiKeys { get; set; } = [];
}

public class ParentApiKey
{
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
}

public class LoggingSettings
{
    public string Directory { get; set; } = "logs";
}

public class DashboardSettings
{
    public bool Enabled { get; set; } = true;
    public int Port { get; set; } = 8080;
}

public class StorageSettings
{
    public string Provider { get; set; } = "sqlite";
    public string ConnectionString { get; set; } = "Data Source=data/gatesql.db";
}
