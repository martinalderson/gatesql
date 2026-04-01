using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace DbProxy.Tests.Integration;

public class McpIntegrationTests : IAsyncLifetime
{
    private readonly McpFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<McpClient> CreateMcpClientAsync(string token)
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_fixture.ApiPort}/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = $"Bearer {token}",
            },
        });

        return await McpClient.CreateAsync(transport);
    }

    [Fact]
    public async Task ListTools_ReturnsFourTools()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        await using var client = await CreateMcpClientAsync(token);

        var tools = await client.ListToolsAsync();
        var toolNames = tools.Select(t => t.Name).OrderBy(n => n).ToList();

        Assert.Equal(["describe_table", "get_session_info", "list_tables", "query_database"], toolNames);
    }

    [Fact]
    public async Task ListTables_ReturnsSeededTables()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("list_tables", new Dictionary<string, object?>());
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        var tables = JsonSerializer.Deserialize<JsonElement>(text);

        var names = tables.EnumerateArray().Select(t => t.GetProperty("name").GetString()).OrderBy(n => n).ToList();
        Assert.Contains("orders", names);
        Assert.Contains("products", names);
    }

    [Fact]
    public async Task ListTables_RespectsAllowlist()
    {
        var (token, _) = await _fixture.CreateSessionAsync(allowedTables: ["public.orders"]);
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("list_tables", new Dictionary<string, object?>());
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        var tables = JsonSerializer.Deserialize<JsonElement>(text);

        var names = tables.EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("orders", names);
        Assert.DoesNotContain("products", names);
    }

    [Fact]
    public async Task DescribeTable_ReturnsColumns()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("describe_table", new Dictionary<string, object?>
        {
            ["tableName"] = "orders",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        var table = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("orders", table.GetProperty("name").GetString());
        var columns = table.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("id", columns);
        Assert.Contains("customer", columns);
        Assert.Contains("total", columns);
    }

    [Fact]
    public async Task DescribeTable_BlockedByAllowlist()
    {
        var (token, _) = await _fixture.CreateSessionAsync(allowedTables: ["public.orders"]);
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("describe_table", new Dictionary<string, object?>
        {
            ["tableName"] = "products",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        Assert.Contains("not accessible", text);
    }

    [Fact]
    public async Task QueryDatabase_ReturnsResults()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT customer, total FROM orders ORDER BY total DESC",
            ["purpose"] = "testing query execution",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        var data = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal(3, data.GetProperty("rowCount").GetInt32());
        Assert.False(data.GetProperty("truncated").GetBoolean());

        var columns = data.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Equal(["customer", "total"], columns);
    }

    [Fact]
    public async Task QueryDatabase_ReadOnlyBlocksWrite()
    {
        var (token, _) = await _fixture.CreateSessionAsync(readOnly: true);
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "INSERT INTO orders (customer, total) VALUES ('Eve', 300.00)",
            ["purpose"] = "testing read-only enforcement",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        Assert.Contains("read-only", text);
    }

    [Fact]
    public async Task QueryDatabase_AllowlistBlocksUnauthorizedTable()
    {
        var (token, _) = await _fixture.CreateSessionAsync(allowedTables: ["public.orders"]);
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT * FROM products",
            ["purpose"] = "testing allowlist",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        Assert.Contains("not accessible", text);
    }

    [Fact]
    public async Task QueryDatabase_BudgetExhaustion()
    {
        var (token, _) = await _fixture.CreateSessionAsync(queryBudget: 2);
        await using var client = await CreateMcpClientAsync(token);

        // Use up the budget
        await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 1",
            ["purpose"] = "query 1",
        });
        await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 2",
            ["purpose"] = "query 2",
        });

        // Third should fail
        var result = await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "SELECT 3",
            ["purpose"] = "query 3",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        Assert.Contains("budget exhausted", text);
    }

    [Fact]
    public async Task QueryDatabase_DangerousQueryBlocked()
    {
        var (token, _) = await _fixture.CreateSessionAsync();
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("query_database", new Dictionary<string, object?>
        {
            ["sql"] = "DELETE FROM orders",
            ["purpose"] = "testing dangerous query blocking",
        });
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        Assert.Contains("dangerous", text.ToLower());
    }

    [Fact]
    public async Task GetSessionInfo_ReturnsBudgetAndPermissions()
    {
        var (token, _) = await _fixture.CreateSessionAsync(
            agentId: "info-agent", task: "info-task", queryBudget: 50, readOnly: true);
        await using var client = await CreateMcpClientAsync(token);

        var result = await client.CallToolAsync("get_session_info", new Dictionary<string, object?>());
        var text = ((TextContentBlock)result.Content.First(c => c is TextContentBlock)).Text;
        var info = JsonSerializer.Deserialize<JsonElement>(text);

        Assert.Equal("info-agent", info.GetProperty("agentId").GetString());
        Assert.Equal("info-task", info.GetProperty("task").GetString());
        Assert.Equal(50, info.GetProperty("queryBudget").GetInt32());
        Assert.Equal(0, info.GetProperty("queriesUsed").GetInt32());
        Assert.Equal(50, info.GetProperty("queriesRemaining").GetInt32());
        Assert.True(info.GetProperty("isReadOnly").GetBoolean());
        Assert.True(info.GetProperty("timeRemainingSeconds").GetInt32() > 0);
    }

    [Fact]
    public async Task McpEndpoint_RejectsNoAuth()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_fixture.ApiPort}/mcp"),
        });

        // Should fail during initialization because auth middleware rejects the request
        await Assert.ThrowsAnyAsync<Exception>(() => McpClient.CreateAsync(transport));
    }

    [Fact]
    public async Task McpEndpoint_RejectsInvalidToken()
    {
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri($"http://127.0.0.1:{_fixture.ApiPort}/mcp"),
            AdditionalHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bearer invalid-token-here",
            },
        });

        await Assert.ThrowsAnyAsync<Exception>(() => McpClient.CreateAsync(transport));
    }
}
