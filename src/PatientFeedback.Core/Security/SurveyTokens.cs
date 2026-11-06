using System.Buffers.Text;
using System.Security.Cryptography;

namespace PatientFeedback.Security;

/// <summary>A freshly issued token: the plaintext goes into the link exactly once, only the hash is stored.</summary>
public sealed record IssuedToken(string Value, byte[] Hash);

/// <summary>
/// One-time survey link tokens.
/// <list type="bullet">
/// <item>256 bits from the OS CSPRNG, base64url (43 chars): not guessable, safe in a URL path.</item>
/// <item>Only SHA-256(token) is persisted. A database dump does not yield working links; because the token has full
/// entropy, a fast unsalted hash is sufficient (no password-style KDF needed).</item>
/// <item>Single use is enforced by the database: the submit statement consumes the hash with a conditional
/// <c>UPDATE … WHERE token_hash = :h AND status IN (…)</c>, so two concurrent submits cannot both succeed.</item>
/// </list>
/// </summary>
public static class SurveyTokens
{
    public const int TokenBytes = 32;
    public const int EncodedLength = 43;

    public static IssuedToken Issue()
    {
        Span<byte> raw = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(raw);
        var value = Base64Url.EncodeToString(raw);
        return new IssuedToken(value, SHA256.HashData(raw));
    }

    /// <summary>Hashes a token taken from a URL. Returns false for anything that is not a well-formed token.</summary>
    public static bool TryHash(string? token, out byte[] hash)
    {
        hash = [];
        if (token is null || token.Length != EncodedLength)
        {
            return false;
        }

        Span<byte> raw = stackalloc byte[TokenBytes];
        if (!Base64Url.IsValid(token) || Base64Url.DecodeFromChars(token, raw) != TokenBytes)
        {
            return false;
        }

        hash = SHA256.HashData(raw);
        return true;
    }
}
