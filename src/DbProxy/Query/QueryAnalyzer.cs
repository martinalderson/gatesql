using PgSqlParser;

namespace DbProxy.Query;

public enum StatementType
{
    Read,        // SELECT, EXPLAIN, SHOW
    Write,       // INSERT, UPDATE, DELETE, MERGE
    Ddl,         // CREATE, ALTER, DROP, TRUNCATE
    Transaction, // BEGIN, COMMIT, ROLLBACK
    Utility,     // SET, DISCARD, VACUUM, COPY, etc.
    Unknown,
}

public record QueryAnalysis
{
    public StatementType Type { get; init; }
    public HashSet<string> TableNames { get; init; } = [];
    public bool IsDangerous { get; init; }
    public string? DangerReason { get; init; }
}

public static class QueryAnalyzer
{
    public static QueryAnalysis Analyze(string sql)
    {
        var result = Parser.Parse(sql);
        if (!result.IsSuccess || result.Value == null)
            return new QueryAnalysis { Type = StatementType.Unknown };

        var parsed = result.Value;
        if (parsed.Stmts.Count == 0)
            return new QueryAnalysis { Type = StatementType.Unknown };

        var worstType = StatementType.Read;
        var allTables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var isDangerous = false;
        string? dangerReason = null;

        foreach (var rawStmt in parsed.Stmts)
        {
            var stmtType = ClassifyStatement(rawStmt.Stmt, allTables, ref isDangerous, ref dangerReason);
            if (StmtPriority(stmtType) > StmtPriority(worstType))
                worstType = stmtType;
        }

        return new QueryAnalysis
        {
            Type = worstType,
            TableNames = allTables,
            IsDangerous = isDangerous,
            DangerReason = dangerReason,
        };
    }

    private static int StmtPriority(StatementType t) => t switch
    {
        StatementType.Read => 0,
        StatementType.Utility => 1,
        StatementType.Transaction => 1,
        StatementType.Unknown => 2,
        StatementType.Write => 3,
        StatementType.Ddl => 4,
        _ => 0,
    };

    private static StatementType ClassifyStatement(Node node, HashSet<string> tables,
        ref bool isDangerous, ref string? dangerReason)
    {
        switch (node.NodeCase)
        {
            case Node.NodeOneofCase.SelectStmt:
                var sel = node.SelectStmt;
                CollectTablesFromSelectStmt(sel, tables);
                // Check CTEs for hidden writes
                var cteType = CheckCtesForWrites(sel, tables, ref isDangerous, ref dangerReason);
                return cteType ?? StatementType.Read;

            case Node.NodeOneofCase.InsertStmt:
                var ins = node.InsertStmt;
                AddRangeVar(ins.Relation, tables);
                if (ins.SelectStmt != null)
                    CollectTablesFromNode(ins.SelectStmt, tables);
                return StatementType.Write;

            case Node.NodeOneofCase.UpdateStmt:
                var upd = node.UpdateStmt;
                AddRangeVar(upd.Relation, tables);
                CollectTablesFromNodes(upd.FromClause, tables);
                if (upd.WhereClause == null)
                {
                    isDangerous = true;
                    dangerReason ??= "UPDATE without WHERE clause";
                }
                return StatementType.Write;

            case Node.NodeOneofCase.DeleteStmt:
                var del = node.DeleteStmt;
                AddRangeVar(del.Relation, tables);
                if (del.WhereClause == null)
                {
                    isDangerous = true;
                    dangerReason ??= "DELETE without WHERE clause";
                }
                return StatementType.Write;

            case Node.NodeOneofCase.MergeStmt:
                var merge = node.MergeStmt;
                AddRangeVar(merge.Relation, tables);
                if (merge.SourceRelation != null)
                    CollectTablesFromNode(merge.SourceRelation, tables);
                return StatementType.Write;

            case Node.NodeOneofCase.DropStmt:
                isDangerous = true;
                dangerReason ??= "DROP statement";
                CollectTablesFromDropStmt(node.DropStmt, tables);
                return StatementType.Ddl;

            case Node.NodeOneofCase.TruncateStmt:
                isDangerous = true;
                dangerReason ??= "TRUNCATE statement";
                foreach (var rel in node.TruncateStmt.Relations)
                    CollectTablesFromNode(rel, tables);
                return StatementType.Ddl;

            case Node.NodeOneofCase.CreateStmt:
                AddRangeVar(node.CreateStmt.Relation, tables);
                return StatementType.Ddl;

            case Node.NodeOneofCase.AlterTableStmt:
                AddRangeVar(node.AlterTableStmt.Relation, tables);
                return StatementType.Ddl;

            case Node.NodeOneofCase.IndexStmt:
                AddRangeVar(node.IndexStmt.Relation, tables);
                return StatementType.Ddl;

            case Node.NodeOneofCase.TransactionStmt:
                return StatementType.Transaction;

            case Node.NodeOneofCase.VariableSetStmt:
            case Node.NodeOneofCase.VariableShowStmt:
            case Node.NodeOneofCase.DiscardStmt:
            case Node.NodeOneofCase.VacuumStmt:
                return StatementType.Utility;

            case Node.NodeOneofCase.ExplainStmt:
                if (node.ExplainStmt.Query != null)
                    CollectTablesFromNode(node.ExplainStmt.Query, tables);
                return StatementType.Read;

            default:
                return StatementType.Unknown;
        }
    }

    private static StatementType? CheckCtesForWrites(SelectStmt sel, HashSet<string> tables,
        ref bool isDangerous, ref string? dangerReason)
    {
        if (sel.WithClause == null)
            return null;

        StatementType? worstCteType = null;
        foreach (var cte in sel.WithClause.Ctes)
        {
            if (cte.NodeCase != Node.NodeOneofCase.CommonTableExpr)
                continue;

            var cteQuery = cte.CommonTableExpr.Ctequery;
            if (cteQuery == null)
                continue;

            var cteStmtType = ClassifyStatement(cteQuery, tables, ref isDangerous, ref dangerReason);
            if (cteStmtType is StatementType.Write or StatementType.Ddl)
            {
                if (worstCteType == null || StmtPriority(cteStmtType) > StmtPriority(worstCteType.Value))
                    worstCteType = cteStmtType;
            }
        }

        return worstCteType;
    }

    private static void CollectTablesFromSelectStmt(SelectStmt sel, HashSet<string> tables)
    {
        CollectTablesFromNodes(sel.FromClause, tables);
        if (sel.WhereClause != null)
            CollectTablesFromNode(sel.WhereClause, tables);
        CollectTablesFromNodes(sel.TargetList, tables);

        // UNION / INTERSECT / EXCEPT
        if (sel.Larg != null)
            CollectTablesFromSelectStmt(sel.Larg, tables);
        if (sel.Rarg != null)
            CollectTablesFromSelectStmt(sel.Rarg, tables);
    }

    private static void CollectTablesFromNodes(Google.Protobuf.Collections.RepeatedField<Node> nodes, HashSet<string> tables)
    {
        foreach (var n in nodes)
            CollectTablesFromNode(n, tables);
    }

    private static void CollectTablesFromNode(Node node, HashSet<string> tables)
    {
        switch (node.NodeCase)
        {
            case Node.NodeOneofCase.RangeVar:
                AddRangeVar(node.RangeVar, tables);
                break;
            case Node.NodeOneofCase.JoinExpr:
                if (node.JoinExpr.Larg != null)
                    CollectTablesFromNode(node.JoinExpr.Larg, tables);
                if (node.JoinExpr.Rarg != null)
                    CollectTablesFromNode(node.JoinExpr.Rarg, tables);
                break;
            case Node.NodeOneofCase.RangeSubselect:
                if (node.RangeSubselect.Subquery != null)
                    CollectTablesFromNode(node.RangeSubselect.Subquery, tables);
                break;
            case Node.NodeOneofCase.SelectStmt:
                CollectTablesFromSelectStmt(node.SelectStmt, tables);
                break;
            case Node.NodeOneofCase.SubLink:
                if (node.SubLink.Subselect != null)
                    CollectTablesFromNode(node.SubLink.Subselect, tables);
                break;
        }
    }

    private static void AddRangeVar(RangeVar? rv, HashSet<string> tables)
    {
        if (rv == null || string.IsNullOrEmpty(rv.Relname))
            return;

        if (!string.IsNullOrEmpty(rv.Schemaname))
            tables.Add($"{rv.Schemaname}.{rv.Relname}");
        else
            tables.Add(rv.Relname);
    }

    private static void CollectTablesFromDropStmt(DropStmt drop, HashSet<string> tables)
    {
        foreach (var obj in drop.Objects)
        {
            if (obj.NodeCase == Node.NodeOneofCase.List)
            {
                // Table name is the last item in the list; schema is earlier items
                var items = obj.List.Items;
                if (items.Count > 0)
                {
                    var last = items[^1];
                    if (last.NodeCase == Node.NodeOneofCase.String)
                    {
                        if (items.Count > 1 && items[0].NodeCase == Node.NodeOneofCase.String)
                            tables.Add($"{items[0].String.Sval}.{last.String.Sval}");
                        else
                            tables.Add(last.String.Sval);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Check whether all tables in a query are allowed by the given allowlist.
    /// Returns null if all tables are allowed, or the first disallowed table name.
    /// </summary>
    public static string? CheckTableAllowlist(HashSet<string> queryTables, List<string> allowedTables)
    {
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in allowedTables)
        {
            allowed.Add(t);
            // Also add the unqualified name if schema-qualified
            var dot = t.IndexOf('.');
            if (dot >= 0)
                allowed.Add(t[(dot + 1)..]);
        }

        foreach (var table in queryTables)
        {
            if (!allowed.Contains(table))
            {
                // Also check if query uses schema.table but allowlist has just table (or vice versa)
                var dot = table.IndexOf('.');
                var unqualified = dot >= 0 ? table[(dot + 1)..] : table;
                if (!allowed.Contains(unqualified))
                    return table;
            }
        }

        return null;
    }
}
