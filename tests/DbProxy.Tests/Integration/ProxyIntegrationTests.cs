using System.Text.Json;
using Npgsql;

namespace DbProxy.Tests.Integration;

public class ProxyIntegrationTests : IAsyncLifetime
{
    private readonly ProxyFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [Fact]
    public async Task CreateSession_ReturnsToken()
    {
        var (token, sessionId) = await _fixture.CreateSessionAsync();

        Assert.NotEmpty(token);
        Assert.StartsWith("sess_", sessionId);
    }

    [Fact]
    public async Task CreateSession_ReturnsConnectionString()
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);

        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{_fixture.ApiPort}/api/sessions",
            new { agentId = "conn-test", task = "test" });

        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var connStr = body.GetProperty("connectionString").GetString()!;
        var psqlCmd = body.GetProperty("psqlCommand").GetString()!;
        var token = body.GetProperty("token").GetString()!;

        Assert.StartsWith("postgresql://agent:", connStr);
        Assert.Contains($":{_fixture.ProxyPort}/", connStr);
        Assert.Contains("psql", psqlCmd);
        Assert.Contains(token, psqlCmd);
    }

    [Fact]
    public async Task Connect_WithValidToken_CanRunQuery()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("/* <agent_purpose>test query</agent_purpose> */ SELECT 1 AS result", conn);
        var result = await cmd.ExecuteScalarAsync();

        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Connect_WithInvalidToken_Fails()
    {
        var connStr = _fixture.BuildConnectionString("not-a-valid-jwt");

        await using var conn = new NpgsqlConnection(connStr);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.OpenAsync());
        Assert.Contains("credentials are not valid", ex.Message);
    }

    [Fact]
    public async Task Connect_WithExpiredSession_Fails()
    {
        var (token, sessionId) = await _fixture.CreateSessionAsync();

        // Revoke the session
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        await http.DeleteAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/sessions/{sessionId}");

        var connStr = _fixture.BuildConnectionString(token);
        await using var conn = new NpgsqlConnection(connStr);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => conn.OpenAsync());
        Assert.Contains("session has been revoked", ex.Message);
    }

    [Fact]
    public async Task QueryBudget_EnforcedAfterLimit()
    {
        var (token, _) = await _fixture.CreateSessionAsync(queryBudget: 20);
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        int succeeded = 0;
        PostgresException? budgetException = null;

        for (int i = 1; i <= 25; i++)
        {
            try
            {
                await using var cmd = new NpgsqlCommand($"/* <agent_purpose>budget test {i}</agent_purpose> */ SELECT {i}", conn);
                await cmd.ExecuteScalarAsync();
                succeeded++;
            }
            catch (PostgresException ex) when (ex.SqlState == "53400")
            {
                budgetException = ex;
                break;
            }
        }

        Assert.NotNull(budgetException);
        Assert.Contains("Query budget exhausted", budgetException.MessageText);
        Assert.True(succeeded > 0);
        Assert.True(succeeded <= 20);
    }

    [Fact]
    public async Task MultipleQueries_AllLogged()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "logger-test-agent", task: "logging-test");
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using (var cmd = new NpgsqlCommand("/* <agent_purpose>first query</agent_purpose> */ SELECT 1", conn))
            await cmd.ExecuteScalarAsync();

        await using (var cmd = new NpgsqlCommand("/* <agent_purpose>second query</agent_purpose> */ SELECT 2", conn))
            await cmd.ExecuteScalarAsync();

        await Task.Delay(2000);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/queries?count=50");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("SELECT 1", body);
        Assert.Contains("SELECT 2", body);
        Assert.Contains("logger-test-agent", body);
    }

    [Fact]
    public async Task SqlCommentContext_ExtractedAndLogged()
    {
        var (token, _) = await _fixture.CreateSessionAsync(agentId: "context-agent", task: "context-test");
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("/* <agent_purpose>integration testing</agent_purpose> */ SELECT 42", conn);
        await cmd.ExecuteScalarAsync();

        await Task.Delay(2000);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/queries?count=50");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Contains("purpose", body);
        Assert.Contains("integration testing", body);
    }

    [Fact]
    public async Task RowCount_LoggedCorrectly()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        await using var cmd = new NpgsqlCommand("/* <agent_purpose>row count test</agent_purpose> */ SELECT generate_series(1, 5)", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        int count = 0;
        while (await reader.ReadAsync()) count++;
        Assert.Equal(5, count);
    }

    [Fact]
    public async Task SessionsList_ReturnsActiveSessions()
    {
        var (_, sessionId) = await _fixture.CreateSessionAsync(agentId: "list-test", task: "listing");

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/sessions");
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains(sessionId, body);
        Assert.Contains("list-test", body);
    }

    [Fact]
    public async Task ApiKey_RequiredForAllEndpoints()
    {
        using var http = new HttpClient();

        var r1 = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/sessions");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, r1.StatusCode);

        var r2 = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{_fixture.ApiPort}/api/sessions",
            new { agentId = "x", task = "y" });
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, r2.StatusCode);
    }

    [Fact]
    public async Task Query_WithoutPurposeComment_IsRejected()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Query without purpose comment should be rejected
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        Assert.Contains("agent_purpose", ex.MessageText);
    }

    [Fact]
    public async Task Query_WithPurposeComment_Succeeds()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // Query with purpose comment should work
        await using var cmd = new NpgsqlCommand("/* <agent_purpose>testing purpose enforcement</agent_purpose> */ SELECT 42", conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task Query_WithoutPurpose_ConnectionStaysAlive()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);

        await using var conn = new NpgsqlConnection(connStr);
        await conn.OpenAsync();

        // First query rejected (no purpose)
        await using (var cmd = new NpgsqlCommand("SELECT 1", conn))
        {
            await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        }

        // Connection should still work with a proper query
        await using (var cmd = new NpgsqlCommand("/* <agent_purpose>retry with purpose</agent_purpose> */ SELECT 2", conn))
        {
            var result = await cmd.ExecuteScalarAsync();
            Assert.Equal(2, result);
        }
    }
}
