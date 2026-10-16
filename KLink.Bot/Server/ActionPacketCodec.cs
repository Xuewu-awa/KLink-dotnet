using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace KLink.Bot.Server;

/// <summary>
/// Encodes and decodes fyserver action packets.
/// Packet layout and key-length table match KLink.Server's ActionCipher.
/// </summary>
public static class ActionPacketCodec
{
    private static readonly int[] KeyLengths =
    {
        47, 53, 73, 55, 61, 103, 47, 103, 33, 45, 73, 37, 97, 71, 39, 71,
        31, 61, 83, 101, 53, 97, 79, 75, 37, 31, 33, 69, 43, 63, 39, 43,
        79, 55, 49, 73, 83, 67, 59, 69, 103, 39, 47, 37, 41, 71, 89, 55,
        49, 45, 33, 45, 69, 49, 43, 53, 59, 31, 59, 101, 61, 41, 79, 75,
        83, 89, 75, 67, 41, 89, 63, 101, 67, 63, 97,
    };

    public sealed record Decoded(int ActionId, JsonObject Payload);

    public static string Encode(int actionId, JsonObject payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if ((uint)actionId > 0xFFFFFF)
        {
            throw new ArgumentOutOfRangeException(nameof(actionId), "Action ID must fit the 24-bit packet header.");
        }

        int keyIndex = RandomNumberGenerator.GetInt32(KeyLengths.Length);
        string key = RandomKey(KeyLengths[keyIndex]);
        byte[] actionBytes =
        {
            (byte)(actionId >> 16),
            (byte)(actionId >> 8),
            (byte)actionId,
        };
        byte[] headerBytes = new byte[3];
        for (int i = 0; i < headerBytes.Length; i++)
        {
            headerBytes[i] = (byte)(actionBytes[i] ^ key[i]);
        }

        string header = Convert.ToBase64String(headerBytes)[..4];
        byte[] plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
        if (plain.Length > 999_999)
        {
            throw new ArgumentException("Action JSON exceeds the six-digit packet length field.", nameof(payload));
        }

        byte[] encrypted = new byte[plain.Length];
        for (int i = 0; i < plain.Length; i++)
        {
            encrypted[i] = (byte)(plain[i] ^ key[i % key.Length]);
        }

        string body = Convert.ToBase64String(encrypted).TrimEnd('=');
        return $"{keyIndex:00}{plain.Length:000000}{header}{key}{body}";
    }

    public static Decoded Decode(string packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Length < 12 || !int.TryParse(packet.AsSpan(0, 2), out int keyIndex)
            || !int.TryParse(packet.AsSpan(2, 6), out int length)
            || (uint)keyIndex >= KeyLengths.Length || length < 0)
        {
            throw new FormatException("Invalid fyserver action packet header.");
        }

        int keyLength = KeyLengths[keyIndex];
        int bodyOffset = 12 + keyLength;
        if (packet.Length < bodyOffset)
        {
            throw new FormatException("Truncated fyserver action packet key.");
        }

        string header = packet.Substring(8, 4);
        string key = packet.Substring(12, keyLength);
        byte[] encrypted = LooseBase64Decode(packet[bodyOffset..]);
        if (encrypted.Length < length)
        {
            throw new FormatException("Truncated fyserver action packet body.");
        }

        byte[] plain = new byte[length];
        for (int i = 0; i < plain.Length; i++)
        {
            plain[i] = (byte)(encrypted[i] ^ key[i % key.Length]);
        }

        byte[] encodedActionId = LooseBase64Decode(header + "==");
        if (encodedActionId.Length < 3)
        {
            throw new FormatException("Invalid fyserver action packet ID header.");
        }

        int actionId = ((encodedActionId[0] ^ key[0]) << 16)
                     | ((encodedActionId[1] ^ key[1]) << 8)
                     | (encodedActionId[2] ^ key[2]);
        JsonObject payload;
        try
        {
            payload = JsonNode.Parse(Encoding.UTF8.GetString(plain)) as JsonObject
                      ?? throw new FormatException("Action packet payload is not a JSON object.");
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw new FormatException("Invalid JSON in fyserver action packet.", ex);
        }

        return new Decoded(actionId, payload);
    }

    private static string RandomKey(int length)
    {
        var key = new StringBuilder(length);
        while (key.Length < length)
        {
            int value = 33 + RandomNumberGenerator.GetInt32(94);
            if (value is not ('"' or '\'' or '\\'))
            {
                key.Append((char)value);
            }
        }

        return key.ToString();
    }

    private static byte[] LooseBase64Decode(string value)
    {
        string input = value.TrimEnd('=');
        using var output = new MemoryStream();
        int offset = 0;
        while (offset < input.Length)
        {
            int packed = 0;
            int count = 0;
            for (int i = 0; i < 4; i++)
            {
                if (offset >= input.Length)
                {
                    break;
                }

                int decoded = DecodeBase64Char(input[offset++]);
                if (decoded < 0)
                {
                    i--;
                    continue;
                }

                packed = (packed << 6) | decoded;
                count++;
            }

            for (int i = count; i < 4; i++)
            {
                packed <<= 6;
            }

            if (count >= 2) output.WriteByte((byte)(packed >> 16));
            if (count >= 3) output.WriteByte((byte)(packed >> 8));
            if (count >= 4) output.WriteByte((byte)packed);
        }

        return output.ToArray();
    }

    private static int DecodeBase64Char(char c) => c switch
    {
        >= 'A' and <= 'Z' => c - 'A',
        >= 'a' and <= 'z' => c - 'a' + 26,
        >= '0' and <= '9' => c - '0' + 52,
        '+' => 62,
        '/' => 63,
        _ => -1,
    };
}
