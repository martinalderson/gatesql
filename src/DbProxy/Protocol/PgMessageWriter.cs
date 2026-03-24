using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DbProxy.Protocol;

public static class PgMessageWriter
{
    public static async Task WriteMessageAsync(Stream stream, byte type, byte[] payload, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(5 + payload.Length);
        try
        {
            buffer[0] = type;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), payload.Length + 4);
            payload.AsSpan().CopyTo(buffer.AsSpan(5));
            await stream.WriteAsync(buffer.AsMemory(0, 5 + payload.Length), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task WriteRawAsync(Stream stream, byte type, byte[] payload, int payloadLength, CancellationToken ct = default)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(5 + payloadLength);
        try
        {
            buffer[0] = type;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), payloadLength + 4);
            payload.AsSpan(0, payloadLength).CopyTo(buffer.AsSpan(5));
            await stream.WriteAsync(buffer.AsMemory(0, 5 + payloadLength), ct);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public static async Task WriteSslResponseAsync(Stream stream, bool supported, CancellationToken ct = default)
    {
        await stream.WriteAsync(new[] { supported ? (byte)'S' : (byte)'N' }, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteAuthCleartextPasswordAsync(Stream stream, CancellationToken ct = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, PgMessageTypes.AuthCleartextPassword);
        await WriteMessageAsync(stream, PgMessageTypes.ServerAuth, payload, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteAuthOkAsync(Stream stream, CancellationToken ct = default)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(payload, PgMessageTypes.AuthOk);
        await WriteMessageAsync(stream, PgMessageTypes.ServerAuth, payload, ct);
    }

    public static async Task WriteBackendKeyDataAsync(Stream stream, int processId, int secretKey, CancellationToken ct = default)
    {
        var payload = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(0), processId);
        BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(4), secretKey);
        await WriteMessageAsync(stream, PgMessageTypes.ServerBackendKeyData, payload, ct);
    }

    public static async Task WriteParameterStatusAsync(Stream stream, string name, string value, CancellationToken ct = default)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        var valueBytes = Encoding.UTF8.GetBytes(value + "\0");
        var payload = new byte[nameBytes.Length + valueBytes.Length];
        nameBytes.CopyTo(payload, 0);
        valueBytes.CopyTo(payload, nameBytes.Length);
        await WriteMessageAsync(stream, PgMessageTypes.ServerParameterStatus, payload, ct);
    }

    public static async Task WriteReadyForQueryAsync(Stream stream, byte status = PgMessageTypes.TransactionIdle, CancellationToken ct = default)
    {
        await WriteMessageAsync(stream, PgMessageTypes.ServerReadyForQuery, [status], ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteErrorResponseAsync(Stream stream, string severity, string code, string message, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        void WriteField(byte fieldType, string value)
        {
            ms.WriteByte(fieldType);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            ms.Write(bytes);
        }

        WriteField((byte)'S', severity);
        WriteField((byte)'V', severity);
        WriteField((byte)'C', code);
        WriteField((byte)'M', message);
        ms.WriteByte(0);

        await WriteMessageAsync(stream, PgMessageTypes.ServerErrorResponse, ms.ToArray(), ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteNoticeResponseAsync(Stream stream, string message, CancellationToken ct = default)
    {
        using var ms = new MemoryStream();

        void WriteField(byte fieldType, string value)
        {
            ms.WriteByte(fieldType);
            var bytes = Encoding.UTF8.GetBytes(value + "\0");
            ms.Write(bytes);
        }

        WriteField((byte)'S', "NOTICE");
        WriteField((byte)'V', "NOTICE");
        WriteField((byte)'C', "00000");
        WriteField((byte)'M', message);
        ms.WriteByte(0);

        await WriteMessageAsync(stream, PgMessageTypes.ServerNoticeResponse, ms.ToArray(), ct);
    }

    public static byte[] BuildSslRequestMessage()
    {
        var buf = new byte[8];
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(0), 8);
        BinaryPrimitives.WriteInt32BigEndian(buf.AsSpan(4), PgMessageTypes.SslRequestCode);
        return buf;
    }

    public static byte[] BuildStartupMessage(string user, string database)
    {
        using var ms = new MemoryStream();
        ms.Write(new byte[4]);
        var versionBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBuf, PgMessageTypes.ProtocolVersion30);
        ms.Write(versionBuf);
        WriteParam(ms, "user", user);
        WriteParam(ms, "database", database);
        ms.WriteByte(0);

        var result = ms.ToArray();
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(0), result.Length);
        return result;
    }

    public static byte[] BuildPasswordMessage(string password)
    {
        var passwordBytes = Encoding.UTF8.GetBytes(password + "\0");
        var buffer = new byte[1 + 4 + passwordBytes.Length];
        buffer[0] = PgMessageTypes.ClientPassword;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), passwordBytes.Length + 4);
        passwordBytes.CopyTo(buffer, 5);
        return buffer;
    }

    public static byte[] BuildSaslInitialResponseMessage(string mechanism, byte[] clientFirstMessage)
    {
        var mechBytes = Encoding.UTF8.GetBytes(mechanism + "\0");
        // 'p' + int32 length + mechanism\0 + int32 response-length + response
        var totalPayload = mechBytes.Length + 4 + clientFirstMessage.Length;
        var buffer = new byte[1 + 4 + totalPayload];
        buffer[0] = PgMessageTypes.ClientPassword;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), totalPayload + 4);
        mechBytes.CopyTo(buffer, 5);
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(5 + mechBytes.Length), clientFirstMessage.Length);
        clientFirstMessage.CopyTo(buffer, 5 + mechBytes.Length + 4);
        return buffer;
    }

    public static byte[] BuildSaslResponseMessage(byte[] clientFinalMessage)
    {
        var buffer = new byte[1 + 4 + clientFinalMessage.Length];
        buffer[0] = PgMessageTypes.ClientPassword;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), clientFinalMessage.Length + 4);
        clientFinalMessage.CopyTo(buffer, 5);
        return buffer;
    }

    private static void WriteParam(MemoryStream ms, string key, string value)
    {
        ms.Write(Encoding.UTF8.GetBytes(key + "\0"));
        ms.Write(Encoding.UTF8.GetBytes(value + "\0"));
    }
}
