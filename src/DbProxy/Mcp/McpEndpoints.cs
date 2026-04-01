using DbProxy.Auth;
using DbProxy.Configuration;
using ModelContextProtocol.AspNetCore;

namespace DbProxy.Mcp;

public static class McpEndpoints
{
    public static WebApplication MapMcpServer(this WebApplication app, ProxyConfig config,
        JwtAuthenticator jwtAuth, SessionManager sessionManager)
    {
        if (!config.Mcp.Enabled)
            return app;

        // Auth middleware runs before MCP handler for /mcp path
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/mcp"))
            {
                await next();
                return;
            }

            var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
            if (string.IsNullOrEmpty(authHeader) || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error":"Missing or invalid Authorization header. Use: Bearer <session-jwt>"}""");
                return;
            }

            var token = authHeader["Bearer ".Length..].Trim();
            var claims = jwtAuth.ValidateToken(token);
            if (claims == null)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error":"Invalid or expired JWT token"}""");
                return;
            }

            var session = sessionManager.GetSession(claims.SessionId);
            if (session == null)
            {
                context.Response.StatusCode = 401;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync("""{"error":"Session not found"}""");
                return;
            }

            var invalidReason = sessionManager.GetInvalidReason(claims.SessionId);
            if (invalidReason != null)
            {
                context.Response.StatusCode = 403;
                context.Response.ContentType = "application/json";
                await context.Response.WriteAsync($$"""{"error":"Session is {{invalidReason}}"}""");
                return;
            }

            var mcpContext = context.RequestServices.GetRequiredService<McpSessionContext>();
            mcpContext.Session = session;
            mcpContext.Claims = claims;

            sessionManager.TouchSession(claims.SessionId);

            await next();
        });

        app.MapMcp("/mcp");

        return app;
    }
}
