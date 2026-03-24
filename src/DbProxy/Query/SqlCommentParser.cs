using System.Text.RegularExpressions;

namespace DbProxy.Query;

public static partial class SqlCommentParser
{
    [GeneratedRegex(@"/\*\s*(\w+)\s*:\s*([^*]*?)\s*\*/", RegexOptions.Compiled)]
    private static partial Regex KeyValuePattern();

    [GeneratedRegex(@"/\*.*?<agent_purpose>(.*?)</agent_purpose>.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    private static partial Regex AgentPurposePattern();

    public static string? ExtractPurpose(string sql)
    {
        var match = AgentPurposePattern().Match(sql);
        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    public static Dictionary<string, string> ExtractContext(string sql)
    {
        var context = new Dictionary<string, string>();

        var purpose = ExtractPurpose(sql);
        if (purpose != null)
            context["purpose"] = purpose;

        var matches = KeyValuePattern().Matches(sql);
        foreach (Match match in matches)
        {
            var key = match.Groups[1].Value.Trim();
            var value = match.Groups[2].Value.Trim();
            if (!string.IsNullOrEmpty(key))
                context[key] = value;
        }

        return context;
    }

    public static bool IsBudgetCheck(string sql)
    {
        return sql.Contains("/* budget:check */", StringComparison.OrdinalIgnoreCase);
    }
}
