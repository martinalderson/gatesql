using System.Net;
using System.Net.Http.Json;

namespace DbProxy.Tests.Integration;

public class SchemaDiscoveryTests : IAsyncLifetime
{
    private readonly ProxyFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private HttpClient CreateClient()
    {
        var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);
        return http;
    }

    [Fact]
    public async Task GetSchema_ReturnsMarkdown()
    {
        // Create a table so there's something to discover
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "/* <agent_purpose>setup</agent_purpose> */ CREATE TABLE IF NOT EXISTS test_schema_discovery (id serial PRIMARY KEY, name text NOT NULL)", conn);
        await cmd.ExecuteNonQueryAsync();

        using var http = CreateClient();
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/schema");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("test_schema_discovery", body);
        Assert.Contains("| Column | Type | Nullable |", body);
        Assert.Contains("id", body);
        Assert.Contains("name", body);
    }

    [Fact]
    public async Task GetSchema_RequiresApiKey()
    {
        using var http = new HttpClient();
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/schema");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task PutAnnotations_ThenGetSchema_IncludesAnnotations()
    {
        // Create a table
        var (token, _) = await _fixture.CreateSessionAsync();
        var connStr = _fixture.BuildConnectionString(token);
        await using var conn = new Npgsql.NpgsqlConnection(connStr);
        await conn.OpenAsync();
        await using var cmd = new Npgsql.NpgsqlCommand(
            "/* <agent_purpose>setup</agent_purpose> */ CREATE TABLE IF NOT EXISTS test_annotations (id serial PRIMARY KEY, email text)", conn);
        await cmd.ExecuteNonQueryAsync();

        using var http = CreateClient();

        // Save annotations
        var putResponse = await http.PutAsJsonAsync(
            $"http://127.0.0.1:{_fixture.ApiPort}/api/schema/public.test_annotations/annotations",
            new { description = "User emails for testing", notes = "Always filter by id for performance" });
        Assert.True(putResponse.IsSuccessStatusCode);

        // Verify annotations appear in schema markdown
        var schemaResponse = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/schema");
        var body = await schemaResponse.Content.ReadAsStringAsync();
        Assert.Contains("User emails for testing", body);
        Assert.Contains("Always filter by id for performance", body);
    }

    [Fact]
    public async Task GetAnnotations_ReturnsStoredData()
    {
        using var http = CreateClient();

        // Save
        await http.PutAsJsonAsync(
            $"http://127.0.0.1:{_fixture.ApiPort}/api/schema/public.my_table/annotations",
            new { description = "My table desc", exampleQueries = "SELECT * FROM my_table", notes = "Join on id" });

        // Read back
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/schema/public.my_table/annotations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("My table desc", body);
        Assert.Contains("SELECT * FROM my_table", body);
        Assert.Contains("Join on id", body);
    }

    [Fact]
    public async Task GetAnnotations_NonexistentTable_ReturnsNulls()
    {
        using var http = CreateClient();
        var response = await http.GetAsync($"http://127.0.0.1:{_fixture.ApiPort}/api/schema/public.nonexistent/annotations");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
