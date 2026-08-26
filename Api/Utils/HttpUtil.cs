using System.Text;

namespace Api.Utils;

public static class HttpUtil
{
    public static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? "{}" : body;
    }

    public static bool TryParseVersion(HttpRequest request, out int? version)
    {
        version = null;

        if (!request.Headers.TryGetValue("X-Action-Version", out var values))
            return true;

        var raw = values.ToString();
        if (!int.TryParse(raw, out var v) || v < 1)
            return false;

        version = v;
        return true;
    }
}