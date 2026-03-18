using System.Buffers.Binary;
using System.Text;

namespace DbProxy.Protocol;

public static class PgMessageWriter
{
    public static async Task WriteMessageAsync(Stream stream, byte type, byte[] payload, CancellationToken ct = default)
    {
        var buffer = new byte[1 + 4 + payload.Length];
        buffer[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(1), payload.Length + 4);
        payload.CopyTo(buffer.AsSpan(5));
        await stream.WriteAsync(buffer, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task WriteRawAsync(Stream stream, byte type, byte[] payload, CancellationToken ct = default)
    {
        // Write type byte + original length prefix + payload (for forwarding messages as-is)
        var header = new byte[5];
        header[0] = type;
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(1), payload.Length + 4);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
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
        ms.WriteByte(0); // terminator

        await WriteMessageAsync(stream, PgMessageTypes.ServerErrorResponse, ms.ToArray(), ct);
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

    public static byte[] BuildStartupMessage(string user, string database)
    {
        using var ms = new MemoryStream();
        // Length placeholder (4 bytes) - will fill in at the end
        ms.Write(new byte[4]);
        // Protocol version 3.0
        var versionBuf = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(versionBuf, PgMessageTypes.ProtocolVersion30);
        ms.Write(versionBuf);
        // Parameters
        WriteParam(ms, "user", user);
        WriteParam(ms, "database", database);
        ms.WriteByte(0); // end marker

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

    private static void WriteParam(MemoryStream ms, string key, string value)
    {
        ms.Write(Encoding.UTF8.GetBytes(key + "\0"));
        ms.Write(Encoding.UTF8.GetBytes(value + "\0"));
    }
}
