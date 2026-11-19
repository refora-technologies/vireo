using System.Text;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math.EC.Rfc8032;

namespace Vireo.Services;

/// <summary>
/// Checks minisign signatures (https://jedisct1.github.io/minisign/), the format Refora signs with: Ed25519, over the exact
/// bytes of a file, plus a "trusted comment" that is signed too (Vireo puts the file's name and version there, so an older
/// file cannot be passed off as a newer one). Only checking lives here: the secret key never comes near Vireo.
/// </summary>
public static class Minisign
{
    public sealed record PublicKey(byte[] KeyId, byte[] Key);

    /// <summary>A public key, from a minisign .pub file's text or just its base64 line; null if it is not one.</summary>
    public static PublicKey? ParsePublicKey(string text)
    {
        foreach (var line in Lines(text))
        {
            if (line.StartsWith("untrusted comment:", StringComparison.Ordinal)) continue;
            var raw = FromBase64(line);
            if (raw is { Length: 42 } && raw[0] == (byte)'E' && raw[1] == (byte)'d') return new PublicKey(raw[2..10], raw[10..42]);
            return null;
        }
        return null;
    }

    /// <summary>
    /// Whether <paramref name="signatureText"/> (a .minisig file) is <paramref name="key"/>'s signature of
    /// <paramref name="message"/>; <paramref name="trustedComment"/> is the signed comment, to be checked by the caller.
    /// </summary>
    public static bool Verify(PublicKey key, ReadOnlySpan<byte> message, string signatureText, out string trustedComment)
    {
        trustedComment = "";
        var lines = Lines(signatureText).ToList();
        if (lines.Count != 4 || !lines[0].StartsWith("untrusted comment:", StringComparison.Ordinal) || !lines[2].StartsWith("trusted comment: ", StringComparison.Ordinal)) return false;
        var sig = FromBase64(lines[1]);
        var global = FromBase64(lines[3]);
        if (sig is not { Length: 74 } || global is not { Length: 64 }) return false;
        var algorithm = Encoding.ASCII.GetString(sig, 0, 2);
        if (algorithm is not ("ED" or "Ed")) return false;
        if (!sig.AsSpan(2, 8).SequenceEqual(key.KeyId)) return false; // signed with some other key
        var signature = sig[10..74];

        // "ED": the signature is over the file's BLAKE2b-512 (minisign's default); "Ed": over the file itself.
        byte[] signed;
        if (algorithm == "ED") signed = Blake2b512(message);
        else signed = message.ToArray();
        if (!Ed25519.Verify(signature, 0, key.Key, 0, signed, 0, signed.Length)) return false;

        var comment = lines[2]["trusted comment: ".Length..];
        var commentBytes = Encoding.UTF8.GetBytes(comment);
        var both = new byte[64 + commentBytes.Length];
        signature.CopyTo(both, 0);
        commentBytes.CopyTo(both, 64);
        if (!Ed25519.Verify(global, 0, key.Key, 0, both, 0, both.Length)) return false;
        trustedComment = comment;
        return true;
    }

    public static byte[] Blake2b512(ReadOnlySpan<byte> data)
    {
        var digest = new Blake2bDigest(512);
        digest.BlockUpdate(data);
        var hash = new byte[64];
        digest.DoFinal(hash, 0);
        return hash;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0);

    private static byte[]? FromBase64(string s)
    {
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }
}
