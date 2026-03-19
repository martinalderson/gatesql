using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace DbProxy.Protocol;

internal static class ScramSha256Authenticator
{
    private const string Mechanism = "SCRAM-SHA-256";

    public static async Task AuthenticateAsync(
        Stream stream, string username, string password,
        ReadOnlyMemory<byte> mechanismListPayload, CancellationToken ct)
    {
        // Step 1: Verify server offers SCRAM-SHA-256
        var mechanisms = ParseMechanismList(mechanismListPayload.Span);
        if (!mechanisms.Contains(Mechanism))
            throw new InvalidOperationException(
                $"Server does not offer {Mechanism}. Available: {string.Join(", ", mechanisms)}");

        // Step 2: Build and send client-first-message
        var clientNonce = GenerateNonce();
        var clientFirstMessageBare = $"n=*,r={clientNonce}";
        var clientFirstMessage = $"n,,{clientFirstMessageBare}";
        var clientFirstBytes = Encoding.UTF8.GetBytes(clientFirstMessage);

        var saslInit = PgMessageWriter.BuildSaslInitialResponseMessage(Mechanism, clientFirstBytes);
        await stream.WriteAsync(saslInit, ct);
        await stream.FlushAsync(ct);

        // Step 3: Read AuthSaslContinue (11) — server-first-message
        var hdrBuf = new byte[5];
        var msg = await PgMessageReader.ReadMessageAsync(stream, hdrBuf, ct)
            ?? throw new InvalidOperationException("Connection closed during SCRAM auth");

        var pLen = PgMessageReader.GetPayloadLength(hdrBuf);
        var authType = BinaryPrimitives.ReadInt32BigEndian(msg.payload.AsSpan(0, 4));
        if (authType != PgMessageTypes.AuthSaslContinue)
            throw new InvalidOperationException($"Expected AuthSaslContinue (11), got auth type {authType}");

        var serverFirstMessage = Encoding.UTF8.GetString(msg.payload.AsSpan(4, pLen - 4));
        PgMessageReader.ReturnPayload(msg.payload);

        var serverFirst = ParseServerFirstMessage(serverFirstMessage);

        // Validate server nonce starts with our client nonce
        if (!serverFirst.Nonce.StartsWith(clientNonce))
            throw new InvalidOperationException("Server nonce does not start with client nonce");

        // Step 4: Compute proof and build client-final-message
        var salt = Convert.FromBase64String(serverFirst.Salt);
        var saltedPassword = DeriveKey(password, salt, serverFirst.Iterations);

        var clientKey = HmacSha256(saltedPassword, "Client Key"u8);
        var storedKey = SHA256.HashData(clientKey);

        // channel binding = base64("n,,") = "biws"
        var clientFinalWithoutProof = $"c=biws,r={serverFirst.Nonce}";
        var authMessage = $"{clientFirstMessageBare},{serverFirstMessage},{clientFinalWithoutProof}";
        var authMessageBytes = Encoding.UTF8.GetBytes(authMessage);

        var clientSignature = HmacSha256(storedKey, authMessageBytes);
        var clientProof = Xor(clientKey, clientSignature);

        var serverKey = HmacSha256(saltedPassword, "Server Key"u8);
        var expectedServerSignature = HmacSha256(serverKey, authMessageBytes);

        var clientFinalMessage = $"{clientFinalWithoutProof},p={Convert.ToBase64String(clientProof)}";
        var clientFinalBytes = Encoding.UTF8.GetBytes(clientFinalMessage);

        var saslResponse = PgMessageWriter.BuildSaslResponseMessage(clientFinalBytes);
        await stream.WriteAsync(saslResponse, ct);
        await stream.FlushAsync(ct);

        // Step 5: Read AuthSaslFinal (12) — verify server signature
        msg = await PgMessageReader.ReadMessageAsync(stream, hdrBuf, ct)
            ?? throw new InvalidOperationException("Connection closed during SCRAM auth");

        pLen = PgMessageReader.GetPayloadLength(hdrBuf);
        authType = BinaryPrimitives.ReadInt32BigEndian(msg.payload.AsSpan(0, 4));
        if (authType != PgMessageTypes.AuthSaslFinal)
            throw new InvalidOperationException($"Expected AuthSaslFinal (12), got auth type {authType}");

        var serverFinalMessage = Encoding.UTF8.GetString(msg.payload.AsSpan(4, pLen - 4));
        PgMessageReader.ReturnPayload(msg.payload);

        if (!serverFinalMessage.StartsWith("v="))
            throw new InvalidOperationException("Server final message missing verifier");

        var serverSignature = Convert.FromBase64String(serverFinalMessage[2..]);
        if (!CryptographicOperations.FixedTimeEquals(serverSignature, expectedServerSignature))
            throw new InvalidOperationException("Server signature mismatch — mutual authentication failed");
    }

    private static List<string> ParseMechanismList(ReadOnlySpan<byte> payload)
    {
        var mechanisms = new List<string>();
        var start = 0;
        for (var i = 0; i < payload.Length; i++)
        {
            if (payload[i] != 0) continue;
            if (i > start)
                mechanisms.Add(Encoding.UTF8.GetString(payload[start..i]));
            start = i + 1;
        }
        return mechanisms;
    }

    private static string GenerateNonce()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    internal static byte[] DeriveKey(string password, byte[] salt, int iterations)
    {
        return Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);
    }

    internal static byte[] HmacSha256(byte[] key, ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data.ToArray());
    }

    private static byte[] Xor(byte[] a, byte[] b)
    {
        var result = new byte[a.Length];
        for (var i = 0; i < a.Length; i++)
            result[i] = (byte)(a[i] ^ b[i]);
        return result;
    }

    private record ServerFirstMessage(string Nonce, string Salt, int Iterations);

    private static ServerFirstMessage ParseServerFirstMessage(string message)
    {
        string? nonce = null, salt = null;
        int iterations = 0;

        foreach (var part in message.Split(','))
        {
            if (part.StartsWith("r=")) nonce = part[2..];
            else if (part.StartsWith("s=")) salt = part[2..];
            else if (part.StartsWith("i=")) iterations = int.Parse(part[2..]);
        }

        if (nonce == null || salt == null || iterations == 0)
            throw new InvalidOperationException($"Malformed server-first-message: {message}");

        return new ServerFirstMessage(nonce, salt, iterations);
    }
}
