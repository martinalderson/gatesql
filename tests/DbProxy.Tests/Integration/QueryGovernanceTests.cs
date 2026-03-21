using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;

namespace DbProxy.Tests.Integration;

public class QueryGovernanceTests : IAsyncLifetime
{
    private readonly ProxyFixture _fixture = new();

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    private async Task<(string token, string sessionId)> CreateSessionAsync(
        bool readOnly = false, string dangerousQueryMode = "block", List<string>? allowedTables = null,
        int? queryBudget = null)
    {
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);

        var response = await http.PostAsJsonAsync(
            $"http://127.0.0.1:{_fixture.ApiPort}/api/sessions",
            new
            {
                agentId = "governance-test",
                task = "testing governance",
                queryBudget,
                readOnly,
                dangerousQueryMode,
                allowedTables,
            });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        return (body.GetProperty("token").GetString()!, body.GetProperty("sessionId").GetString()!);
    }

    private async Task<NpgsqlConnection> ConnectAsync(string token)
    {
        var conn = new NpgsqlConnection(_fixture.BuildConnectionString(token));
        await conn.OpenAsync();
        return conn;
    }

    private static string WithPurpose(string sql) =>
        $"/* <agent_purpose>governance test</agent_purpose> */ {sql}";

    // --- Read-Only Mode ---

    [Fact]
    public async Task ReadOnly_SelectSucceeds()
    {
        var (token, _) = await CreateSessionAsync(readOnly: true);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("SELECT 1 AS result"), conn);
        var result = await cmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task ReadOnly_CreateTableRejected()
    {
        var (token, _) = await CreateSessionAsync(readOnly: true);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("CREATE TABLE test_readonly (id int)"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("read-only", ex.MessageText);
    }

    [Fact]
    public async Task ReadOnly_InsertRejected()
    {
        var (token, _) = await CreateSessionAsync(readOnly: true);
        await using var conn = await ConnectAsync(token);

        // Create the table with a non-read-only session first
        var (rwToken, _) = await CreateSessionAsync(readOnly: false);
        await using var rwConn = await ConnectAsync(rwToken);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_ins (id int)"), rwConn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("INSERT INTO test_ins (id) VALUES (1)"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("read-only", ex.MessageText);
    }

    [Fact]
    public async Task ReadOnly_SetAndBeginAllowed()
    {
        var (token, _) = await CreateSessionAsync(readOnly: true);
        await using var conn = await ConnectAsync(token);

        // SET and BEGIN are internal queries, should pass through
        await using var cmd = new NpgsqlCommand("SET client_encoding = 'UTF8'", conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    [Fact]
    public async Task NonReadOnly_InsertSucceeds()
    {
        var (token, _) = await CreateSessionAsync(readOnly: false);
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_write (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();
        await using var cmd = new NpgsqlCommand(WithPurpose("INSERT INTO test_write (id) VALUES (1)"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    // --- Dangerous Query Detection ---

    [Fact]
    public async Task DangerousBlock_DeleteWithoutWhereRejected()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "block");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_danger (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("DELETE FROM test_danger"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("dangerous", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DangerousBlock_DeleteWithWhereAllowed()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "block");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_danger2 (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("DELETE FROM test_danger2 WHERE id = 999"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    [Fact]
    public async Task DangerousBlock_DropTableRejected()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "block");
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("DROP TABLE IF EXISTS nonexistent_table"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("dangerous", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DangerousBlock_TruncateRejected()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "block");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_trunc (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("TRUNCATE test_trunc"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
    }

    [Fact]
    public async Task DangerousBlock_UpdateWithoutWhereRejected()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "block");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_upd (id int, name text)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("UPDATE test_upd SET name = 'x'"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("dangerous", ex.MessageText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DangerousOff_DeleteWithoutWhereAllowed()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "off");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_off (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("DELETE FROM test_off"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    [Fact]
    public async Task DangerousWarn_DeleteWithoutWhereAllowed()
    {
        var (token, _) = await CreateSessionAsync(dangerousQueryMode: "warn");
        await using var conn = await ConnectAsync(token);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS test_warn (id int)"), conn);
        await createCmd.ExecuteNonQueryAsync();

        await using var cmd = new NpgsqlCommand(WithPurpose("DELETE FROM test_warn"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw (warning logged server-side)
    }

    // --- Table Allowlists ---

    [Fact]
    public async Task AllowedTables_AllowedTableSucceeds()
    {
        // First create the table
        var (setupToken, _) = await CreateSessionAsync();
        await using var setupConn = await ConnectAsync(setupToken);
        await using var createCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS allowed_test (id int)"), setupConn);
        await createCmd.ExecuteNonQueryAsync();

        var (token, _) = await CreateSessionAsync(allowedTables: ["allowed_test"]);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("SELECT * FROM allowed_test"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    [Fact]
    public async Task AllowedTables_DisallowedTableRejected()
    {
        // First create both tables
        var (setupToken, _) = await CreateSessionAsync();
        await using var setupConn = await ConnectAsync(setupToken);
        await using var c1 = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS allowed_orders (id int)"), setupConn);
        await c1.ExecuteNonQueryAsync();
        await using var c2 = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS secret_users (id int)"), setupConn);
        await c2.ExecuteNonQueryAsync();

        var (token, _) = await CreateSessionAsync(allowedTables: ["allowed_orders"]);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("SELECT * FROM secret_users"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("not accessible", ex.MessageText);
    }

    [Fact]
    public async Task AllowedTables_JoinWithDisallowedTableRejected()
    {
        var (setupToken, _) = await CreateSessionAsync();
        await using var setupConn = await ConnectAsync(setupToken);
        await using var c1 = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS join_allowed (id int)"), setupConn);
        await c1.ExecuteNonQueryAsync();
        await using var c2 = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS join_secret (id int)"), setupConn);
        await c2.ExecuteNonQueryAsync();

        var (token, _) = await CreateSessionAsync(allowedTables: ["join_allowed"]);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("SELECT * FROM join_allowed a JOIN join_secret s ON a.id = s.id"), conn);
        var ex = await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        Assert.Equal("42501", ex.SqlState);
        Assert.Contains("not accessible", ex.MessageText);
    }

    [Fact]
    public async Task AllowedTables_NullAllowsEverything()
    {
        var (setupToken, _) = await CreateSessionAsync();
        await using var setupConn = await ConnectAsync(setupToken);
        await using var c1 = new NpgsqlCommand(WithPurpose("CREATE TABLE IF NOT EXISTS any_table (id int)"), setupConn);
        await c1.ExecuteNonQueryAsync();

        var (token, _) = await CreateSessionAsync(allowedTables: null);
        await using var conn = await ConnectAsync(token);
        await using var cmd = new NpgsqlCommand(WithPurpose("SELECT * FROM any_table"), conn);
        await cmd.ExecuteNonQueryAsync(); // Should not throw
    }

    // --- Connection stays alive after governance rejection ---

    [Fact]
    public async Task GovernanceRejection_ConnectionSurvives()
    {
        var (token, _) = await CreateSessionAsync(readOnly: true);
        await using var conn = await ConnectAsync(token);

        // First query rejected (write on read-only)
        await using var badCmd = new NpgsqlCommand(WithPurpose("CREATE TABLE should_fail (id int)"), conn);
        await Assert.ThrowsAsync<PostgresException>(() => badCmd.ExecuteNonQueryAsync());

        // Connection should still work for valid queries
        await using var goodCmd = new NpgsqlCommand(WithPurpose("SELECT 1 AS result"), conn);
        var result = await goodCmd.ExecuteScalarAsync();
        Assert.Equal(1, result);
    }

    // --- API returns governance fields ---

    [Fact]
    public async Task SessionsApi_ReturnsGovernanceFields()
    {
        var (_, sessionId) = await CreateSessionAsync(
            readOnly: true,
            dangerousQueryMode: "warn",
            allowedTables: ["orders", "products"]);

        using var http = new HttpClient();
        http.DefaultRequestHeaders.Add("X-Api-Key", _fixture.ApiKey);

        var response = await http.GetFromJsonAsync<JsonElement>($"http://127.0.0.1:{_fixture.ApiPort}/api/sessions");
        var sessions = response.EnumerateArray().ToList();
        var session = sessions.First(s => s.GetProperty("sessionId").GetString() == sessionId);

        Assert.True(session.GetProperty("isReadOnly").GetBoolean());
        Assert.Equal("warn", session.GetProperty("dangerousQueryMode").GetString());
        var tables = session.GetProperty("allowedTables").EnumerateArray().Select(t => t.GetString()).ToList();
        Assert.Contains("orders", tables);
        Assert.Contains("products", tables);
    }
}
