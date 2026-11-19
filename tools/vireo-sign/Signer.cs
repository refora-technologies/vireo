using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Vireo.Signing;

/// <summary>
/// Making Refora's rules key and signing with it, in minisign's format (public key and signatures can be checked with the
/// official minisign tool too). The secret key is kept in a file of our own: the Ed25519 seed, encrypted with AES-256-GCM
/// under a key derived from a password (PBKDF2-SHA256, 600,000 rounds). The password is never stored anywhere.
/// </summary>
public static class Signer
{
    public sealed record KeyPair(string SecretText, string PublicText);

    private sealed record SecretFile(string Format, string KeyId, int Rounds, string Salt, string Nonce, string Sealed, string Tag);

    private const string SecretFormat = "vireo-sign-secret-1";
    private const int Rounds = 600_000;

    public static KeyPair Generate(string password)
    {
        if (password.Length < 12) throw new ArgumentException("Use at least 12 characters.");
        var seed = RandomNumberGenerator.GetBytes(32);
        var keyId = RandomNumberGenerator.GetBytes(8);
        var publicKey = new byte[32];
        Ed25519.GeneratePublicKey(seed, 0, publicKey, 0);

        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(12);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, Rounds, HashAlgorithmName.SHA256, 32);
        var plain = seed.Concat(keyId).ToArray();
        var sealedBytes = new byte[plain.Length];
        var tag = new byte[16];
        using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, plain, sealedBytes, tag);
        CryptographicOperations.ZeroMemory(seed);
        CryptographicOperations.ZeroMemory(plain);
        CryptographicOperations.ZeroMemory(key);

        var secret = new SecretFile(SecretFormat, Convert.ToHexString(keyId), Rounds, Convert.ToBase64String(salt), Convert.ToBase64String(nonce), Convert.ToBase64String(sealedBytes), Convert.ToBase64String(tag));
        var secretText = "Vireo rules key, Refora Technologies (SECRET: never share it, never put it in a repository)\n" + JsonSerializer.Serialize(secret, new JsonSerializerOptions { WriteIndented = true }) + "\n";
        var publicText = $"untrusted comment: minisign public key {Convert.ToHexString(keyId)} (Vireo rules key, Refora Technologies)\n"
            + Convert.ToBase64String([(byte)'E', (byte)'d', .. keyId, .. publicKey]) + "\n";
        return new KeyPair(secretText, publicText);
    }

    /// <summary>The seed and key id, if the password opens the secret key.</summary>
    private static (byte[] Seed, byte[] KeyId) Open(string secretText, string password)
    {
        var json = secretText[secretText.IndexOf('{')..];
        var s = JsonSerializer.Deserialize<SecretFile>(json) ?? throw new CryptographicException("Not a Vireo secret key.");
        if (s.Format != SecretFormat) throw new CryptographicException("Not a Vireo secret key.");
        var key = Rfc2898DeriveBytes.Pbkdf2(password, Convert.FromBase64String(s.Salt), s.Rounds, HashAlgorithmName.SHA256, 32);
        var sealedBytes = Convert.FromBase64String(s.Sealed);
        var plain = new byte[sealedBytes.Length];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(Convert.FromBase64String(s.Nonce), sealedBytes, Convert.FromBase64String(s.Tag), plain);
        }
        catch (AuthenticationTagMismatchException) { throw new CryptographicException("That password does not open the key."); }
        finally { CryptographicOperations.ZeroMemory(key); }
        return (plain[..32], plain[32..40]);
    }

    /// <summary>A minisign signature (prehashed, "ED") of <paramref name="message"/>, with <paramref name="trustedComment"/> signed too.</summary>
    public static string Sign(string secretText, string password, ReadOnlySpan<byte> message, string trustedComment)
    {
        var (seed, keyId) = Open(secretText, password);
        try { return SignWithSeed(seed, keyId, message, trustedComment); }
        finally { CryptographicOperations.ZeroMemory(seed); }
    }

    /// <summary>For tests: signs with a raw key.</summary>
    public static string SignWithSeed(byte[] seed, byte[] keyId, ReadOnlySpan<byte> message, string trustedComment)
    {
        if (trustedComment.Contains('\n') || trustedComment.Contains('\r')) throw new ArgumentException("One line only.");
        var hash = Vireo.Services.Minisign.Blake2b512(message);
        var signature = new byte[64];
        Ed25519.Sign(seed, 0, hash, 0, hash.Length, signature, 0);
        var comment = Encoding.UTF8.GetBytes(trustedComment);
        var both = signature.Concat(comment).ToArray();
        var global = new byte[64];
        Ed25519.Sign(seed, 0, both, 0, both.Length, global, 0);
        return "untrusted comment: signature from the Vireo rules key\n"
            + Convert.ToBase64String([(byte)'E', (byte)'D', .. keyId, .. signature]) + "\n"
            + "trusted comment: " + trustedComment + "\n"
            + Convert.ToBase64String(global) + "\n";
    }

    /// <summary>For tests: a raw key pair and its public key text.</summary>
    public static (byte[] Seed, byte[] KeyId, string PublicText) TestKey()
    {
        var seed = RandomNumberGenerator.GetBytes(32);
        var keyId = RandomNumberGenerator.GetBytes(8);
        var pub = new byte[32];
        Ed25519.GeneratePublicKey(seed, 0, pub, 0);
        return (seed, keyId, Convert.ToBase64String([(byte)'E', (byte)'d', .. keyId, .. pub]));
    }
}
