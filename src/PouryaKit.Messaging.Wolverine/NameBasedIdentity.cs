using System.Security.Cryptography;
using System.Text;

namespace PouryaKit.Messaging.Wolverine;

// The one identity the Kit derives from a text: a name-based UUID (RFC 9562 version 8) from its SHA-256, so every
// delivery of one thing repeats it and two different texts never share it. A Snapshot's identity is one, and so are a
// Saga's step, emission and timeout identities (PouryaKit.Messaging.Wolverine.Sagas).
internal static class NameBasedIdentity
{
    public static Guid Of(string text)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(text), hash);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash[..16], bigEndian: true);
    }
}
