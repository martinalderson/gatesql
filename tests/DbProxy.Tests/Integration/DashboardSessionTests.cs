using System.Net;
using Npgsql;

namespace DbProxy.Tests.Integration;

public class DashboardSessionTests : IAsyncLifetime
{
    private readonly DashboardFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CreateSessionForm_ReturnsFormHtml()
    {
        using var http = _fixture.CreateAuthenticatedClient();
        var response = await http.GetAsync("/dashboard/sessions/new");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("agentId", html);
        Assert.Contains("queryBudget", html);
        Assert.Contains("dangerousQueryMode", html);
        Assert.Contains("Create Session", html);
    }

    [Fact]
    public async Task CreateSession_ReturnsCredentials()
    {
        using var http = _fixture.CreateAuthenticatedClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = "dashboard-test-agent",
            ["task"] = "integration-test",
            ["queryBudget"] = "50",
            ["readOnly"] = "true",
            ["dangerousQueryMode"] = "block",
        });
        var response = await http.PostAsync("/dashboard/sessions/create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Session Created", html);
        Assert.Contains("sess_", html);
        Assert.Contains("PGPASSWORD", html);
        Assert.Contains("postgresql://agent:", html);
    }

    [Fact]
    public async Task CreateSession_TokenIsUsable()
    {
        using var http = _fixture.CreateAuthenticatedClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = "token-test-agent",
            ["task"] = "verify-token",
        });
        var response = await http.PostAsync("/dashboard/sessions/create", form);
        var html = await response.Content.ReadAsStringAsync();

        // Extract token from the HTML (it's in a <code id="cred-token"> element)
        var tokenStart = html.IndexOf("id=\"cred-token\">") + "id=\"cred-token\">".Length;
        var tokenEnd = html.IndexOf("</code>", tokenStart);
        var token = html[tokenStart..tokenEnd];

        Assert.NotEmpty(token);

        // Use token to connect through proxy
        var connStr = _fixture.BuildConnectionString(token);
        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("/* <agent_purpose>dashboard test</agent_purpose> */ SELECT 42", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task CreateSession_WithAllowedTables_ParsesCorrectly()
    {
        using var http = _fixture.CreateAuthenticatedClient();
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = "tables-test",
            ["task"] = "table-test",
            ["allowedTables"] = "public.orders, public.products",
        });
        var response = await http.PostAsync("/dashboard/sessions/create", form);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Session Created", html);

        // Verify via API that tables were parsed
        using var apiHttp = new HttpClient();
        apiHttp.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        var sessionsResponse = await apiHttp.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/sessions");
        var body = await sessionsResponse.Content.ReadAsStringAsync();
        Assert.Contains("public.orders", body);
        Assert.Contains("public.products", body);
    }

    [Fact]
    public async Task CreateSession_WithoutAuth_RedirectsToLogin()
    {
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["agentId"] = "unauth-test",
            ["task"] = "test",
        });
        var response = await http.PostAsync($"http://127.0.0.1:{_fixture.ApiPort}/dashboard/sessions/create", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("login", response.Headers.Location?.ToString() ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dashboard_EmptyState_ShowsForm()
    {
        using var http = _fixture.CreateAuthenticatedClient();
        var response = await http.GetAsync("/dashboard");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        // Empty state should show the create session form
        Assert.Contains("Create Agent Session", html);
        Assert.Contains("No active sessions", html);
    }
}
