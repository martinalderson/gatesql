using DbProxy.Auth;

namespace DbProxy.Mcp;

public class McpSessionContext
{
    private static readonly AsyncLocal<AgentSession?> _session = new();
    private static readonly AsyncLocal<JwtClaims?> _claims = new();

    public AgentSession? Session
    {
        get => _session.Value;
        set => _session.Value = value;
    }

    public JwtClaims? Claims
    {
        get => _claims.Value;
        set => _claims.Value = value;
    }
}
