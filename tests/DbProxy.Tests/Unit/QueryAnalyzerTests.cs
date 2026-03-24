using DbProxy.Query;

namespace DbProxy.Tests.Unit;

public class QueryAnalyzerTests
{
    // --- Statement Type Classification ---

    [Fact]
    public void Select_ClassifiedAsRead()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM orders WHERE id = 1");
        Assert.Equal(StatementType.Read, result.Type);
    }

    [Fact]
    public void ExplainSelect_ClassifiedAsRead()
    {
        var result = QueryAnalyzer.Analyze("EXPLAIN SELECT * FROM orders");
        Assert.Equal(StatementType.Read, result.Type);
    }

    [Fact]
    public void ShowStatement_ClassifiedAsUtility()
    {
        var result = QueryAnalyzer.Analyze("SHOW server_version");
        Assert.Equal(StatementType.Utility, result.Type);
    }

    [Fact]
    public void Insert_ClassifiedAsWrite()
    {
        var result = QueryAnalyzer.Analyze("INSERT INTO orders (name) VALUES ('test')");
        Assert.Equal(StatementType.Write, result.Type);
    }

    [Fact]
    public void Update_ClassifiedAsWrite()
    {
        var result = QueryAnalyzer.Analyze("UPDATE orders SET name = 'x' WHERE id = 1");
        Assert.Equal(StatementType.Write, result.Type);
    }

    [Fact]
    public void Delete_ClassifiedAsWrite()
    {
        var result = QueryAnalyzer.Analyze("DELETE FROM orders WHERE id = 1");
        Assert.Equal(StatementType.Write, result.Type);
    }

    [Fact]
    public void DropTable_ClassifiedAsDdl()
    {
        var result = QueryAnalyzer.Analyze("DROP TABLE orders");
        Assert.Equal(StatementType.Ddl, result.Type);
    }

    [Fact]
    public void CreateTable_ClassifiedAsDdl()
    {
        var result = QueryAnalyzer.Analyze("CREATE TABLE foo (id int)");
        Assert.Equal(StatementType.Ddl, result.Type);
    }

    [Fact]
    public void Truncate_ClassifiedAsDdl()
    {
        var result = QueryAnalyzer.Analyze("TRUNCATE orders");
        Assert.Equal(StatementType.Ddl, result.Type);
    }

    [Fact]
    public void Begin_ClassifiedAsTransaction()
    {
        var result = QueryAnalyzer.Analyze("BEGIN");
        Assert.Equal(StatementType.Transaction, result.Type);
    }

    [Fact]
    public void SetStatement_ClassifiedAsUtility()
    {
        var result = QueryAnalyzer.Analyze("SET client_encoding = 'UTF8'");
        Assert.Equal(StatementType.Utility, result.Type);
    }

    // --- Table Extraction ---

    [Fact]
    public void Select_ExtractsTableName()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM orders");
        Assert.Contains("orders", result.TableNames);
    }

    [Fact]
    public void Select_ExtractsSchemaQualifiedTable()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM public.orders");
        Assert.Contains("public.orders", result.TableNames);
    }

    [Fact]
    public void Join_ExtractsAllTables()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM orders o JOIN products p ON o.product_id = p.id");
        Assert.Contains("orders", result.TableNames);
        Assert.Contains("products", result.TableNames);
    }

    [Fact]
    public void Insert_ExtractsTargetTable()
    {
        var result = QueryAnalyzer.Analyze("INSERT INTO orders (name) VALUES ('test')");
        Assert.Contains("orders", result.TableNames);
    }

    [Fact]
    public void Delete_ExtractsTargetTable()
    {
        var result = QueryAnalyzer.Analyze("DELETE FROM orders WHERE id = 1");
        Assert.Contains("orders", result.TableNames);
    }

    [Fact]
    public void Update_ExtractsTargetTable()
    {
        var result = QueryAnalyzer.Analyze("UPDATE orders SET name = 'x' WHERE id = 1");
        Assert.Contains("orders", result.TableNames);
    }

    [Fact]
    public void Subquery_ExtractsInnerTable()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM (SELECT * FROM secret_table) s");
        Assert.Contains("secret_table", result.TableNames);
    }

    [Fact]
    public void DropTable_ExtractsTableName()
    {
        var result = QueryAnalyzer.Analyze("DROP TABLE orders");
        Assert.Contains("orders", result.TableNames);
    }

    // --- Dangerous Query Detection ---

    [Fact]
    public void DeleteWithoutWhere_IsDangerous()
    {
        var result = QueryAnalyzer.Analyze("DELETE FROM orders");
        Assert.True(result.IsDangerous);
        Assert.Contains("DELETE without WHERE", result.DangerReason);
    }

    [Fact]
    public void DeleteWithWhere_IsNotDangerous()
    {
        var result = QueryAnalyzer.Analyze("DELETE FROM orders WHERE id = 1");
        Assert.False(result.IsDangerous);
    }

    [Fact]
    public void UpdateWithoutWhere_IsDangerous()
    {
        var result = QueryAnalyzer.Analyze("UPDATE orders SET name = 'x'");
        Assert.True(result.IsDangerous);
        Assert.Contains("UPDATE without WHERE", result.DangerReason);
    }

    [Fact]
    public void UpdateWithWhere_IsNotDangerous()
    {
        var result = QueryAnalyzer.Analyze("UPDATE orders SET name = 'x' WHERE id = 1");
        Assert.False(result.IsDangerous);
    }

    [Fact]
    public void DropTable_IsDangerous()
    {
        var result = QueryAnalyzer.Analyze("DROP TABLE orders");
        Assert.True(result.IsDangerous);
        Assert.Contains("DROP", result.DangerReason);
    }

    [Fact]
    public void Truncate_IsDangerous()
    {
        var result = QueryAnalyzer.Analyze("TRUNCATE orders");
        Assert.True(result.IsDangerous);
        Assert.Contains("TRUNCATE", result.DangerReason);
    }

    [Fact]
    public void Select_IsNotDangerous()
    {
        var result = QueryAnalyzer.Analyze("SELECT * FROM orders");
        Assert.False(result.IsDangerous);
    }

    // --- Edge Cases ---

    [Fact]
    public void MultiStatement_TakesWorstType()
    {
        var result = QueryAnalyzer.Analyze("SELECT 1; DROP TABLE orders");
        Assert.Equal(StatementType.Ddl, result.Type);
        Assert.True(result.IsDangerous);
    }

    [Fact]
    public void CteWithDelete_ClassifiedAsWrite()
    {
        var result = QueryAnalyzer.Analyze("WITH d AS (DELETE FROM orders RETURNING *) SELECT * FROM d");
        Assert.Equal(StatementType.Write, result.Type);
    }

    [Fact]
    public void CteWithDeleteWithoutWhere_IsDangerous()
    {
        var result = QueryAnalyzer.Analyze("WITH d AS (DELETE FROM orders RETURNING *) SELECT * FROM d");
        Assert.True(result.IsDangerous);
    }

    [Fact]
    public void PurposeCommentedQuery_ParsesCorrectly()
    {
        var result = QueryAnalyzer.Analyze("/* <agent_purpose>test</agent_purpose> */ SELECT * FROM orders WHERE id = 1");
        Assert.Equal(StatementType.Read, result.Type);
        Assert.Contains("orders", result.TableNames);
    }

    [Fact]
    public void InvalidSql_ReturnsUnknown()
    {
        var result = QueryAnalyzer.Analyze("NOT VALID SQL AT ALL !!!");
        Assert.Equal(StatementType.Unknown, result.Type);
        Assert.False(result.IsDangerous);
    }

    [Fact]
    public void EmptyString_ReturnsUnknown()
    {
        var result = QueryAnalyzer.Analyze("");
        Assert.Equal(StatementType.Unknown, result.Type);
    }

    // --- Table Allowlist ---

    [Fact]
    public void CheckTableAllowlist_AllowedTable_ReturnsNull()
    {
        var queryTables = new HashSet<string> { "orders" };
        var allowed = new List<string> { "orders", "products" };
        Assert.Null(QueryAnalyzer.CheckTableAllowlist(queryTables, allowed));
    }

    [Fact]
    public void CheckTableAllowlist_DisallowedTable_ReturnsTableName()
    {
        var queryTables = new HashSet<string> { "orders", "users" };
        var allowed = new List<string> { "orders", "products" };
        Assert.Equal("users", QueryAnalyzer.CheckTableAllowlist(queryTables, allowed));
    }

    [Fact]
    public void CheckTableAllowlist_SchemaQualifiedMatch()
    {
        var queryTables = new HashSet<string> { "orders" };
        var allowed = new List<string> { "public.orders" };
        Assert.Null(QueryAnalyzer.CheckTableAllowlist(queryTables, allowed));
    }

    [Fact]
    public void CheckTableAllowlist_QueryUsesSchemaAllowlistDoesNot()
    {
        var queryTables = new HashSet<string> { "public.orders" };
        var allowed = new List<string> { "orders" };
        // "public.orders" unqualified is "orders" which is in the allowlist
        Assert.Null(QueryAnalyzer.CheckTableAllowlist(queryTables, allowed));
    }
}
