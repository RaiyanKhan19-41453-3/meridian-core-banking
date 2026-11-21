using System.Security.Cryptography;
using System.Text;

namespace CoreBanking.Api.Services;

/// <summary>
/// AES-256-GCM encryption for PII blobs (currently KYC document bytes).
/// Ciphertext is framed as: "ENC1" + 12-byte nonce + tag + ciphertext.
/// Legacy plaintext rows without the header remain readable.
/// </summary>
public sealed class EncryptionService
{
    private static readonly byte[] Header = "ENC1"u8.ToArray();
    private readonly byte[] _key;

    public EncryptionService(IConfiguration config)
    {
        var raw = config["Security:EncryptionKey"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException("Security:EncryptionKey is not configured.");
        _key = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    public byte[] Encrypt(byte[] plaintext)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintext, ciphertext, tag);

        var framed = new byte[Header.Length + nonce.Length + tag.Length + ciphertext.Length];
        Header.CopyTo(framed, 0);
        nonce.CopyTo(framed, Header.Length);
        tag.CopyTo(framed, Header.Length + nonce.Length);
        ciphertext.CopyTo(framed, Header.Length + nonce.Length + tag.Length);
        return framed;
    }

    public byte[] Decrypt(byte[] stored)
    {
        if (stored.Length < Header.Length || !stored.AsSpan(0, Header.Length).SequenceEqual(Header))
            return stored; // legacy plaintext row
        var offset = Header.Length;
        var nonce = stored.AsSpan(offset, 12); offset += 12;
        var tag = stored.AsSpan(offset, 16); offset += 16;
        var ciphertext = stored.AsSpan(offset);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return plaintext;
    }
}
