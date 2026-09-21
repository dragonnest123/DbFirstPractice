using System.Security.Cryptography;
using System.Text;

namespace Api.Services;

public sealed class SignatureVerifier
{
    private readonly byte[]? _secret;

    public SignatureVerifier(IConfiguration configuration)
    {
        var secret = configuration["PROVIDER_HMAC_SECRET"]
            ?? Environment.GetEnvironmentVariable("PROVIDER_HMAC_SECRET");
        _secret = string.IsNullOrEmpty(secret) ? null : Encoding.UTF8.GetBytes(secret);
    }
    
    public bool TryVerify(byte[] body, string? header, out int? version)
    {
        version = null;
        if (string.IsNullOrEmpty(header))
            return true;

        var value = header.Trim();
        const string prefix = "v1=";
        if (!value.StartsWith(prefix, StringComparison.Ordinal))
            return false;

        var hex = value.Substring(prefix.Length);
        if (hex.Length != 64 || !IsLowerHex(hex) || _secret is null)
            return false;

        using var hmac = new HMACSHA256(_secret);
        var digest = Convert.ToHexString(hmac.ComputeHash(body)).ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(digest), Encoding.ASCII.GetBytes(hex)))
            return false;

        version = 1;
        return true;
    }

    private static bool IsLowerHex(string value)
    {
        foreach (var c in value)
        {
            var isLowerDigit = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!isLowerDigit)
                return false;
        }
        return true;
    }
}