using System.ComponentModel.DataAnnotations;

namespace DbProxy.Data;

public class QueryLogEntity
{
    [Key]
    public long Id { get; set; }
    public DateTime Timestamp { get; set; }
    public required string AgentId { get; set; }
    public required string SessionId { get; set; }
    public required string Task { get; set; }
    public required string Query { get; set; }
    public string? Context { get; set; } // JSON
    public int? RowCount { get; set; }
    public long DurationMs { get; set; }
    public bool Success { get; set; }
    public string? Error { get; set; }
}
