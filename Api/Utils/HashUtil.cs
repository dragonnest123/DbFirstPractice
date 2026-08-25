using System.Security.Cryptography;
using System.Text;

namespace Api.Utils;

public static class HashUtil
{
    public static string Sha256Hex(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
