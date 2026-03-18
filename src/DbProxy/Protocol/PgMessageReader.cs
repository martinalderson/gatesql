using System.Buffers.Binary;
using System.Text;

namespace DbProxy.Protocol;

public static class PgMessageReader
{
    public static async Task<(byte type, byte[] payload)?> ReadMessageAsync(Stream stream, CancellationToken ct = default)
    {
        // Read type + length in a single 5-byte read
        var header = new byte[5];
        if (await ReadExactAsync(stream, header, ct) != 5)
            return null;

        int length = BinaryPrimitives.ReadInt32BigEndian(header.AsSpan(1));
        int payloadLength = length - 4;

        if (payloadLength < 0 || payloadLength > 100 * 1024 * 1024)
            throw new InvalidOperationException($"Invalid message length: {length}");

        var payload = new byte[payloadLength];
        if (payloadLength > 0 && await ReadExactAsync(stream, payload, ct) != payloadLength)
            return null;

        return (header[0], payload);
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
            var key = ReadNullTerminatedString(payload, ref offset);
            if (string.IsNullOrEmpty(key))
                break;
            var value = ReadNullTerminatedString(payload, ref offset);
            parameters[key] = value;
        }

        return (version, parameters);
    }

    public static string ReadPasswordFromPayload(byte[] payload)
    {
        int offset = 0;
        return ReadNullTerminatedString(payload, ref offset);
    }

    public static string ReadQueryFromPayload(byte[] payload)
    {
        int offset = 0;
        return ReadNullTerminatedString(payload, ref offset);
    }

    public static string ReadParseStatementFromPayload(byte[] payload)
    {
        int offset = 0;
        _ = ReadNullTerminatedString(payload, ref offset); // statement name
        return ReadNullTerminatedString(payload, ref offset); // query
    }

    public static string ReadCommandTagFromPayload(byte[] payload)
    {
        int offset = 0;
        return ReadNullTerminatedString(payload, ref offset);
    }

    private static string ReadNullTerminatedString(byte[] data, ref int offset)
    {
        int start = offset;
        while (offset < data.Length && data[offset] != 0)
            offset++;
        var result = Encoding.UTF8.GetString(data, start, offset - start);
        if (offset < data.Length)
            offset++;
        return result;
    }

    private static async Task<int> ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(totalRead), ct);
            if (read == 0)
                return totalRead;
            totalRead += read;
        }
        return totalRead;
    }
}
