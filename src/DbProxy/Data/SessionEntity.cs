using System.ComponentModel.DataAnnotations;

namespace DbProxy.Data;

public class SessionEntity
{
    [Key]
    public required string SessionId { get; set; }
    public required string AgentId { get; set; }
    public required string Task { get; set; }
    public int? QueryBudget { get; set; }
    public int QueriesUsed { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime LastActivityAt { get; set; }
    public bool IsConnected { get; set; }
    public bool IsRevoked { get; set; }
}
