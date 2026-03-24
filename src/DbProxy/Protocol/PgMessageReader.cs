using System.Buffers;
using System.Buffers.Binary;
using System.Text;

namespace DbProxy.Protocol;

public static class PgMessageReader
{
    public static async Task<(byte type, byte[] payload)?> ReadMessageAsync(Stream stream, byte[] headerBuf, CancellationToken ct = default)
    {
        // Read type + length in a single 5-byte read using caller's buffer
        if (await ReadExactAsync(stream, headerBuf.AsMemory(0, 5), ct) != 5)
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(headerBuf.AsSpan(1));
        int payloadLength = length - 4;

        if (payloadLength < 0 || payloadLength > 100 * 1024 * 1024)
            throw new InvalidOperationException($"Invalid message length: {length}");

        byte[] payload;
        if (payloadLength == 0)
        {
            payload = [];
        }
        else
        {
            payload = ArrayPool<byte>.Shared.Rent(payloadLength);
            if (await ReadExactAsync(stream, payload.AsMemory(0, payloadLength), ct) != payloadLength)
            {
                ArrayPool<byte>.Shared.Return(payload);
                return null;
            }
        }

        return (headerBuf[0], payload);
    }

    // Return a rented payload buffer back to the pool
    public static void ReturnPayload(byte[] payload)
    {
        if (payload.Length > 0)
            ArrayPool<byte>.Shared.Return(payload);
    }

    // Get the actual payload length from the header (since rented arrays may be larger)
    public static int GetPayloadLength(byte[] headerBuf)
    {
        return BinaryPrimitives.ReadInt32BigEndian(headerBuf.AsSpan(1)) - 4;
    }

    public static async Task<(int version, Dictionary<string, string> parameters)?> ReadStartupMessageAsync(Stream stream, CancellationToken ct = default)
    {
        var lengthBuf = new byte[4];
        if (await ReadExactAsync(stream, lengthBuf, ct) != 4)
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(lengthBuf);
        int payloadLength = length - 4;

        if (payloadLength < 4 || payloadLength > 10 * 1024)
            throw new InvalidOperationException($"Invalid startup message length: {length}");

        var payload = new byte[payloadLength];
        if (await ReadExactAsync(stream, payload, ct) != payloadLength)
            return null;

        int version = BinaryPrimitives.ReadInt32BigEndian(payload.AsSpan(0, 4));

        if (version == PgMessageTypes.SslRequestCode || version == PgMessageTypes.CancelRequestCode)
            return (version, new Dictionary<string, string>());

        var parameters = new Dictionary<string, string>();
        int offset = 4;
        while (offset < payload.Length)
        {
            var key = ReadNullTerminatedString(payload.AsSpan(), ref offset);
            if (key.Length == 0)
                break;
            var value = ReadNullTerminatedString(payload.AsSpan(), ref offset);
            parameters[key] = value;
        }

        return (version, parameters);
    }

    public static string ReadPasswordFromPayload(ReadOnlySpan<byte> payload)
    {
        int offset = 0;
        return ReadNullTerminatedString(payload, ref offset);
    }

    public static string ReadQueryFromPayload(ReadOnlySpan<byte> payload, int payloadLength)
    {
        int offset = 0;
        return ReadNullTerminatedString(payload[..payloadLength], ref offset);
    }

    public static string ReadParseStatementFromPayload(ReadOnlySpan<byte> payload, int payloadLength)
    {
        int offset = 0;
        var span = payload[..payloadLength];
        _ = ReadNullTerminatedString(span, ref offset); // statement name
        return ReadNullTerminatedString(span, ref offset); // query
    }

    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        var result = Encoding.UTF8.GetString(data[start..offset]);
        if (offset < data.Length)
            offset++;
        return result;
    }

    private static async Task<int> ReadExactAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], ct);
            if (read == 0)
                return totalRead;
            totalRead += read;
        }
        return totalRead;
    }
}
