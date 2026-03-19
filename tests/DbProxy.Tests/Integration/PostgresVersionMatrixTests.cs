using Npgsql;

namespace DbProxy.Tests.Integration;

public class PostgresVersionMatrixTests
{
    public static TheoryData<string> PostgresVersions => new()
    {
        "postgres:14",
        "postgres:15",
        "postgres:16",
        "postgres:17",
    };

    [Theory]
    [MemberData(nameof(PostgresVersions))]
    public async Task ConnectAndQuery_AcrossVersions(string image)
    {
        var fixture = new ProxyFixture(image);
        await fixture.InitializeAsync();
        try
        {
            var (token, sessionId) = await fixture.CreateSessionAsync();
            Assert.StartsWith("sess_", sessionId);

            var connStr = fixture.BuildConnectionString(token);
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync();

            await using var cmd = new NpgsqlCommand(
                "/* <agent_purpose>version matrix test</agent_purpose> */ SELECT version()", conn);
            var result = (string?)(await cmd.ExecuteScalarAsync());

            Assert.NotNull(result);
            // Extract major version from image tag and verify it appears in the version string
            var expectedMajor = image.Split(':')[1];
            Assert.Contains($"PostgreSQL {expectedMajor}", result);
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }
}
