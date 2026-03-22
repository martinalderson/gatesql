namespace DbProxy.Dashboard.Models;

public class SetupViewModel
{
    public string ApiKey { get; init; } = "";
    public int ProxyPort { get; init; }
    public int DashboardPort { get; init; }

    // Upstream form fields
    public string Host { get; init; } = "localhost";
    public int Port { get; init; } = 5432;
    public string Database { get; init; } = "postgres";
    public string Username { get; init; } = "postgres";
    public string Password { get; init; } = "";
    public string SslMode { get; init; } = "disable";

    public bool IsDocker { get; init; }

    public string? Error { get; init; }
    public string? TestResult { get; init; }
    public bool TestSuccess { get; init; }
}
