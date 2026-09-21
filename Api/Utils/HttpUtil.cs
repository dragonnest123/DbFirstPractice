namespace Api.Utils;

public static class HttpUtil
{
    public static async Task<byte[]> ReadBodyBytesAsync(HttpRequest request)
    {
        using var buffer = new MemoryStream();
        await request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
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