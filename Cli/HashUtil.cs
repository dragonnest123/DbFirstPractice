using System.Security.Cryptography;
using System.Text;

namespace Cli;

public static class HashUtil
{
    public static string Sha256Hex(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}