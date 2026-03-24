using DbProxy.Protocol;

namespace DbProxy.Tests.Unit;

public class ExemptionTests
{
    [Theory]
    [InlineData("SET extra_float_digits = 3", "Session command (SET)")]
    [InlineData("SET search_path TO public", "Session command (SET)")]
    [InlineData("set client_encoding = 'UTF8'", "Session command (SET)")]
    public void Set_Commands_AreExempt(string sql, string expectedReason)
    {
        Assert.Equal(expectedReason, PgProtocolHandler.GetExemptionReason(sql));
    }

    [Theory]
    [InlineData("BEGIN", "Transaction control")]
    [InlineData("begin", "Transaction control")]
    [InlineData("COMMIT", "Transaction control")]
    [InlineData("ROLLBACK", "Transaction control")]
    [InlineData("BEGIN TRANSACTION", "Transaction control")]
    public void Transaction_Commands_AreExempt(string sql, string expectedReason)
    {
        Assert.Equal(expectedReason, PgProtocolHandler.GetExemptionReason(sql));
    }

    [Theory]
    [InlineData("DISCARD ALL", "Session command (DISCARD)")]
    [InlineData("discard temp", "Session command (DISCARD)")]
    public void Discard_Commands_AreExempt(string sql, string expectedReason)
    {
        Assert.Equal(expectedReason, PgProtocolHandler.GetExemptionReason(sql));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")]
    public void Empty_Queries_AreExempt(string sql)
    {
        Assert.Equal("Empty query", PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void PureCatalogSelect_IsExempt()
    {
        var sql = "SELECT t.oid, t.typname FROM pg_catalog.pg_type t";
        Assert.Equal("Catalog metadata query", PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void CatalogSelectWithNamespace_IsExempt()
    {
        var sql = "SELECT n.nspname, t.typname FROM pg_catalog.pg_type t JOIN pg_catalog.pg_namespace n ON n.oid = t.typnamespace";
        Assert.Equal("Catalog metadata query", PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void InformationSchemaSelect_IsExempt()
    {
        var sql = "SELECT table_name FROM information_schema.tables";
        Assert.Equal("Catalog metadata query", PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void SelectFromUserTable_NotExempt()
    {
        var sql = "SELECT * FROM orders";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void SelectJoiningUserTableWithCatalog_NotExempt()
    {
        var sql = "SELECT o.*, t.typname FROM orders o JOIN pg_catalog.pg_type t ON true";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void SelectWithPgCatalogInComment_NotExempt()
    {
        var sql = "/* pg_catalog bypass */ SELECT * FROM orders";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void InsertIntoCatalog_NotExempt()
    {
        var sql = "INSERT INTO pg_catalog.pg_type VALUES (1, 'test')";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void DeleteFromCatalog_NotExempt()
    {
        var sql = "DELETE FROM pg_catalog.pg_type WHERE oid = 1";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void Truncate_NotExempt()
    {
        // TRUNCATE was previously incorrectly exempted
        var sql = "TRUNCATE orders";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void Vacuum_NotExempt()
    {
        // VACUUM was previously exempted; now requires purpose for auditability
        var sql = "VACUUM orders";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void ShowCommand_IsExempt()
    {
        var sql = "SHOW server_version";
        Assert.Equal("Session command (SHOW)", PgProtocolHandler.GetExemptionReason(sql));
    }

    [Fact]
    public void SelectWithNoTables_NotExempt()
    {
        // SELECT 1 or SELECT version() have no table references — not catalog queries
        var sql = "SELECT version()";
        Assert.Null(PgProtocolHandler.GetExemptionReason(sql));
    }
}
