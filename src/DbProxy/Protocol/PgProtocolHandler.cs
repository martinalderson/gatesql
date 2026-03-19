using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DbProxy.Auth;
using DbProxy.Configuration;
using DbProxy.Query;

namespace DbProxy.Protocol;

public class PgProtocolHandler : IDisposable
{
    private readonly ProxyConfig _config;
    private readonly JwtAuthenticator _jwtAuth;
    private readonly SessionManager _sessionManager;
    private readonly QueryLogger _queryLogger;
    private readonly TcpListener _listener;
    private readonly X509Certificate2? _tlsCert;
    private readonly ILogger<PgProtocolHandler> _logger;
    private CancellationTokenSource? _cts;

    public PgProtocolHandler(
        ProxyConfig config,
        JwtAuthenticator jwtAuth,
        SessionManager sessionManager,
        QueryLogger queryLogger,
        ILogger<PgProtocolHandler> logger)
    {
        _config = config;
        _jwtAuth = jwtAuth;
        _sessionManager = sessionManager;
        _queryLogger = queryLogger;
        _logger = logger;

        _listener = new TcpListener(IPAddress.Parse(config.Proxy.ListenHost), config.Proxy.ListenPort);

        if (config.Proxy.TlsCertPath != null && config.Proxy.TlsKeyPath != null)
            _tlsCert = X509Certificate2.CreateFromPemFile(config.Proxy.TlsCertPath, config.Proxy.TlsKeyPath);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _logger.LogInformation("PG proxy listening on {Host}:{Port}", _config.Proxy.ListenHost, _config.Proxy.ListenPort);

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                _ = HandleClientAsync(client, _cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            Stream clientStream = client.GetStream();
            string? sessionId = null;

            try
            {
                // Phase 1: Read startup message (might be SSL request)
                var startup = await PgMessageReader.ReadStartupMessageAsync(clientStream, ct);
                if (startup == null)
                    return;

                // Handle SSL request
                if (startup.Value.version == PgMessageTypes.SslRequestCode)
                {
                    if (_tlsCert != null)
                    {
                        await PgMessageWriter.WriteSslResponseAsync(clientStream, true, ct);
                        var sslStream = new SslStream(clientStream, false);
                        await sslStream.AuthenticateAsServerAsync(_tlsCert, false, false);
                        clientStream = sslStream;
                    }
                    else
                    {
                        await PgMessageWriter.WriteSslResponseAsync(clientStream, false, ct);
                    }

                    // Read the real startup message
                    startup = await PgMessageReader.ReadStartupMessageAsync(clientStream, ct);
                    if (startup == null)
                        return;
                }

                if (startup.Value.version != PgMessageTypes.ProtocolVersion30)
                {
                    await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "08004",
                        $"Unsupported protocol version: {startup.Value.version}", ct);
                    return;
                }

                var parameters = startup.Value.parameters;
                _logger.LogInformation("Startup from user={User} database={Db}",
                    parameters.GetValueOrDefault("user", "?"),
                    parameters.GetValueOrDefault("database", "?"));

                // Phase 2: Request password (which is the JWT)
                await PgMessageWriter.WriteAuthCleartextPasswordAsync(clientStream, ct);

                var authHeaderBuf = new byte[5];
                var passwordMsg = await PgMessageReader.ReadMessageAsync(clientStream, authHeaderBuf, ct);
                if (passwordMsg == null || passwordMsg.Value.type != PgMessageTypes.ClientPassword)
                    return;

                var pwPayloadLen = PgMessageReader.GetPayloadLength(authHeaderBuf);
                var jwt = PgMessageReader.ReadPasswordFromPayload(passwordMsg.Value.payload.AsSpan(0, pwPayloadLen));
                PgMessageReader.ReturnPayload(passwordMsg.Value.payload);

                // Phase 3: Validate JWT
                var claims = _jwtAuth.ValidateToken(jwt);
                if (claims == null)
                {
                    await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "28000",
                        "Invalid or expired session token", ct);
                    return;
                }

                sessionId = claims.SessionId;

                if (!_sessionManager.ValidateSession(sessionId))
                {
                    await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "28000",
                        "Session expired, revoked, or idle too long", ct);
                    return;
                }

                var session = _sessionManager.GetSession(sessionId)!;
                session.IsConnected = true;

                _logger.LogInformation("Agent {AgentId} connected (session={SessionId}, task={Task})",
                    claims.AgentId, sessionId, claims.Task);

                // Phase 4: Connect to upstream PostgreSQL
                using var upstreamConnection = await ConnectUpstreamAsync(ct);
                if (upstreamConnection == null)
                {
                    await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "08006",
                        "Failed to connect to upstream database", ct);
                    return;
                }

                // Disable Nagle's algorithm for lower latency on small writes
                if (client.Client != null)
                    client.Client.NoDelay = true;

                // Phase 5: Send auth OK + params + ready to client
                await PgMessageWriter.WriteAuthOkAsync(clientStream, ct);
                await PgMessageWriter.WriteParameterStatusAsync(clientStream, "server_version", "16.0", ct);
                await PgMessageWriter.WriteParameterStatusAsync(clientStream, "server_encoding", "UTF8", ct);
                await PgMessageWriter.WriteParameterStatusAsync(clientStream, "client_encoding", "UTF8", ct);
                await PgMessageWriter.WriteBackendKeyDataAsync(clientStream, Process.GetCurrentProcess().Id, Random.Shared.Next(), ct);
                await PgMessageWriter.WriteReadyForQueryAsync(clientStream, ct: ct);

                // Phase 6: Proxy messages
                await ProxyMessagesAsync(clientStream, upstreamConnection, session, claims, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Error handling client connection (session={SessionId})", sessionId);
            }
            finally
            {
                if (sessionId != null)
                {
                    var session = _sessionManager.GetSession(sessionId);
                    if (session != null)
                        session.IsConnected = false;
                }
            }
        }
    }

    private async Task ProxyMessagesAsync(Stream clientStream, Stream upstreamStream, AgentSession session, JwtClaims claims, CancellationToken ct)
    {
        // Track current query state for logging
        string? currentQuery = null;
        Dictionary<string, string>? currentContext = null;
        var sw = new Stopwatch();
        int rowCount = 0;
        bool errorOccurred = false;

        // Per-connection reusable header buffers
        var clientHeaderBuf = new byte[5];
        var upstreamHeaderBuf = new byte[5];

        // Bidirectional relay: client→upstream in this task, upstream→client in background
        using var proxyCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var upstreamToClient = RelayUpstreamToClientAsync(upstreamStream, clientStream, upstreamHeaderBuf,
            () => rowCount++,
            () => errorOccurred = true,
            () =>
            {
                sw.Stop();
                if (currentQuery != null)
                {
                    _queryLogger.Log(new QueryLogEntry
                    {
                        AgentId = claims.AgentId,
                        SessionId = claims.SessionId,
                        Task = claims.Task,
                        Query = currentQuery,
                        Context = currentContext,
                        RowCount = rowCount,
                        DurationMs = sw.ElapsedMilliseconds,
                        Success = !errorOccurred,
                    });
                }
                currentQuery = null;
                currentContext = null;
                rowCount = 0;
                errorOccurred = false;
            },
            proxyCts.Token);

        try
        {
            bool discardUntilSync = false;
            int messageCount = 0;

            while (!proxyCts.Token.IsCancellationRequested)
            {
                if (++messageCount % 100 == 0 && !_sessionManager.ValidateSession(session.SessionId))
                {
                    await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "57P01",
                        "Session expired or revoked", proxyCts.Token);
                    return;
                }

                var msg = await PgMessageReader.ReadMessageAsync(clientStream, clientHeaderBuf, proxyCts.Token);
                if (msg == null)
                    return;

                var (type, payload) = msg.Value;
                int payloadLength = PgMessageReader.GetPayloadLength(clientHeaderBuf);

                try
                {
                    if (type == PgMessageTypes.ClientTerminate)
                    {
                        await PgMessageWriter.WriteRawAsync(upstreamStream, type, payload, payloadLength, proxyCts.Token);
                        await upstreamStream.FlushAsync(proxyCts.Token);
                        return;
                    }

                    if (discardUntilSync)
                    {
                        if (type == PgMessageTypes.ClientSync)
                        {
                            discardUntilSync = false;
                            await PgMessageWriter.WriteReadyForQueryAsync(clientStream, ct: proxyCts.Token);
                        }
                        continue;
                    }

                    string? queryText = null;
                    if (type == PgMessageTypes.ClientSimpleQuery)
                        queryText = PgMessageReader.ReadQueryFromPayload(payload, payloadLength);
                    else if (type == PgMessageTypes.ClientParse)
                        queryText = PgMessageReader.ReadParseStatementFromPayload(payload, payloadLength);

                    if (queryText != null)
                    {
                        if (!SqlCommentParser.IsBudgetCheck(queryText)
                            && !IsInternalQuery(queryText)
                            && SqlCommentParser.ExtractPurpose(queryText) == null)
                        {
                            await PgMessageWriter.WriteErrorResponseAsync(clientStream, "ERROR", "42000",
                                "Query rejected: missing purpose comment. Add /* <agent_purpose>your reason</agent_purpose> */ to your SQL.", proxyCts.Token);

                            _queryLogger.Log(new QueryLogEntry
                            {
                                AgentId = claims.AgentId,
                                SessionId = claims.SessionId,
                                Task = claims.Task,
                                Query = queryText,
                                RowCount = 0,
                                DurationMs = 0,
                                Success = false,
                                Error = "Missing purpose comment",
                            });

                            if (type == PgMessageTypes.ClientParse)
                                discardUntilSync = true;
                            else
                                await PgMessageWriter.WriteReadyForQueryAsync(clientStream, ct: proxyCts.Token);
                            continue;
                        }

                        if (SqlCommentParser.IsBudgetCheck(queryText))
                        {
                            var remaining = _sessionManager.GetRemainingBudget(session.SessionId);
                            var budgetMsg = remaining.HasValue
                                ? $"Query budget remaining: {remaining.Value}"
                                : "No query budget set (unlimited)";
                            await PgMessageWriter.WriteNoticeResponseAsync(clientStream, budgetMsg, proxyCts.Token);
                        }

                        if (!_sessionManager.IncrementQueryCount(session.SessionId))
                        {
                            _queryLogger.Log(new QueryLogEntry
                            {
                                AgentId = claims.AgentId,
                                SessionId = claims.SessionId,
                                Task = claims.Task,
                                Query = queryText,
                                RowCount = 0,
                                DurationMs = 0,
                                Success = false,
                                Error = "Budget exhausted",
                            });
                            await PgMessageWriter.WriteErrorResponseAsync(clientStream, "FATAL", "53400",
                                "Query budget exhausted for this session", proxyCts.Token);
                            return;
                        }

                        _sessionManager.TouchSession(session.SessionId);

                        currentQuery = queryText;
                        currentContext = SqlCommentParser.ExtractContext(queryText);
                        if (currentContext.Count == 0) currentContext = null;
                        sw.Restart();
                    }

                    // Forward to upstream
                    await PgMessageWriter.WriteRawAsync(upstreamStream, type, payload, payloadLength, proxyCts.Token);
                    await upstreamStream.FlushAsync(proxyCts.Token);
                }
                finally
                {
                    PgMessageReader.ReturnPayload(payload);
                }
            }
        }
        finally
        {
            await proxyCts.CancelAsync();
            try { await upstreamToClient; } catch { }
        }
    }

    private static async Task RelayUpstreamToClientAsync(
        Stream upstreamStream, Stream clientStream, byte[] headerBuf,
        Action onDataRow, Action onError, Action onReadyForQuery,
        CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var msg = await PgMessageReader.ReadMessageAsync(upstreamStream, headerBuf, ct);
                if (msg == null)
                    return;

                var (type, payload) = msg.Value;
                int payloadLength = PgMessageReader.GetPayloadLength(headerBuf);

                try
                {
                    await PgMessageWriter.WriteRawAsync(clientStream, type, payload, payloadLength, ct);

                    if (type == PgMessageTypes.ServerDataRow)
                        onDataRow();
                    else if (type == PgMessageTypes.ServerErrorResponse)
                        onError();
                    else if (type == PgMessageTypes.ServerReadyForQuery)
                    {
                        await clientStream.FlushAsync(ct);
                        onReadyForQuery();
                    }
                }
                finally
                {
                    PgMessageReader.ReturnPayload(payload);
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<Stream?> ConnectUpstreamAsync(CancellationToken ct)
    {
        try
        {
            var client = new TcpClient();
            await client.ConnectAsync(_config.Upstream.Host, _config.Upstream.Port, ct);
            var stream = client.GetStream();

            // Send startup message
            var startupMsg = PgMessageWriter.BuildStartupMessage(
                _config.Upstream.Username,
                _config.Upstream.Database);
            await stream.WriteAsync(startupMsg, ct);
            await stream.FlushAsync(ct);

            // Read auth response
            var hdrBuf = new byte[5];
            while (true)
            {
                var msg = await PgMessageReader.ReadMessageAsync(stream, hdrBuf, ct);
                if (msg == null)
                    return null;

                var (type, payload) = msg.Value;
                var pLen = PgMessageReader.GetPayloadLength(hdrBuf);

                if (type == PgMessageTypes.ServerAuth)
                {
                    int authType = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, pLen));
                    if (authType == PgMessageTypes.AuthCleartextPassword)
                    {
                        var passwordMsg = PgMessageWriter.BuildPasswordMessage(_config.Upstream.Password);
                        await stream.WriteAsync(passwordMsg, ct);
                        await stream.FlushAsync(ct);
                    }
                    else if (authType == PgMessageTypes.AuthMd5Password)
                    {
                        var salt = payload.AsSpan(4, 4).ToArray();
                        var md5Password = ComputeMd5Password(_config.Upstream.Password, _config.Upstream.Username, salt);
                        var passwordMsg = PgMessageWriter.BuildPasswordMessage(md5Password);
                        await stream.WriteAsync(passwordMsg, ct);
                        await stream.FlushAsync(ct);
                    }
                    else if (authType == PgMessageTypes.AuthOk)
                    {
                        // Continue reading until ReadyForQuery
                    }
                    else if (authType == PgMessageTypes.AuthSasl)
                    {
                        var mechList = payload.AsSpan(4, pLen - 4).ToArray();
                        PgMessageReader.ReturnPayload(payload);
                        await ScramSha256Authenticator.AuthenticateAsync(
                            stream, _config.Upstream.Username, _config.Upstream.Password,
                            mechList, ct);
                        // Authenticator handles through AuthSaslFinal;
                        // loop continues to read AuthOk + ReadyForQuery
                        continue;
                    }
                    else
                    {
                        _logger.LogError("Unsupported upstream auth type: {AuthType}", authType);
                        PgMessageReader.ReturnPayload(payload);
                        return null;
                    }
                }
                else if (type == PgMessageTypes.ServerErrorResponse)
                {
                    _logger.LogError("Upstream auth failed");
                    PgMessageReader.ReturnPayload(payload);
                    return null;
                }
                else if (type == PgMessageTypes.ServerReadyForQuery)
                {
                    PgMessageReader.ReturnPayload(payload);
                    return stream;
                }
                // Skip ParameterStatus, BackendKeyData, etc.
                PgMessageReader.ReturnPayload(payload);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to connect to upstream {Host}:{Port}", _config.Upstream.Host, _config.Upstream.Port);
            return null;
        }
    }

    private static bool IsInternalQuery(string sql)
    {
        var trimmed = sql.TrimStart();
        return trimmed.Contains("pg_type", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("pg_catalog", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("pg_namespace", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("pg_range", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("pg_enum", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("SET ", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("DISCARD", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("BEGIN", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("COMMIT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("ROLLBACK", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("VACUUM", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("TRUNCATE", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(trimmed);
    }

    private static string ComputeMd5Password(string password, string username, byte[] salt)
    {
        // PG MD5 auth: "md5" + md5(md5(password + username) + salt)
        var inner = MD5.HashData(Encoding.UTF8.GetBytes(password + username));
        var innerHex = Convert.ToHexString(inner).ToLowerInvariant();
        var outer = MD5.HashData([.. Encoding.UTF8.GetBytes(innerHex), .. salt]);
        return "md5" + Convert.ToHexString(outer).ToLowerInvariant();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _listener.Stop();
        _tlsCert?.Dispose();
        GC.SuppressFinalize(this);
    }
}
