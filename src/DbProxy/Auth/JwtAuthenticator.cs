using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.IdentityModel.Tokens;

namespace DbProxy.Auth;

public record JwtClaims(string SessionId, string AgentId, string Task, int? Budget, DateTime ExpiresAt);

public class JwtAuthenticator
{
    private readonly SigningKeyManager _keyManager;
    private readonly JwtSecurityTokenHandler _tokenHandler = new() { MapInboundClaims = false };
    private readonly TokenValidationParameters _validationParams;

    public JwtAuthenticator(SigningKeyManager keyManager)
    {
        _keyManager = keyManager;
        _validationParams = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "db-proxy",
            ValidateAudience = true,
            ValidAudience = "db-proxy-agent",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = keyManager.SecurityKey,
            ClockSkew = TimeSpan.FromSeconds(30),
        };
    }

    public string GenerateToken(string sessionId, string agentId, string task, int? budget, TimeSpan lifetime)
    {
        var claims = new List<Claim>
        {
            new("sid", sessionId),
            new("sub", agentId),
            new("task", task),
        };

        if (budget.HasValue)
            claims.Add(new Claim("budget", budget.Value.ToString()));

        var token = new JwtSecurityToken(
            issuer: "db-proxy",
            audience: "db-proxy-agent",
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: _keyManager.SigningCredentials
        );

        return _tokenHandler.WriteToken(token);
    }

    public JwtClaims? ValidateToken(string token)
    {
        try
        {
            var principal = _tokenHandler.ValidateToken(token, _validationParams, out var validatedToken);

            var sessionId = principal.FindFirst("sid")?.Value;
            var agentId = principal.FindFirst("sub")?.Value;
            var task = principal.FindFirst("task")?.Value;

            if (sessionId == null || agentId == null || task == null)
                return null;

            int? budget = null;
            var budgetClaim = principal.FindFirst("budget")?.Value;
            if (budgetClaim != null && int.TryParse(budgetClaim, out var budgetValue))
                budget = budgetValue;

            return new JwtClaims(sessionId, agentId, task, budget, validatedToken.ValidTo);
        }
        catch
        {
            return null;
        }
    }
}
