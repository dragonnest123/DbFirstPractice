using System.Text.Json;
using Api.Contracts;

namespace Api.Utils;

public static class Envelope
{
    public static IResult Error(
        string code, 
        string message, 
        bool retryable, 
        string correlationId, 
        int? actionVersion, 
        int http)
        => Results.Json(new
        {
            status = "error",
            code,
            message,
            retryable,
            details = new { },
            meta = new { correlationId, actionVersion }
        }, statusCode: http);

    public static IResult Ok(
        string outcome, 
        JsonElement result, 
        string correlationId, 
        int actionVersion) 
        => Results.Json(new
        {
            status = "ok",
            outcome,
            result,
            meta = new { correlationId, actionVersion }
        }, statusCode: 200);

    public static IResult OkFromStored(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);

        var root = doc.RootElement;
        var outcome = root.TryGetProperty("outcome", out var oc) ? oc.GetString() : null;
        var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        var correlationId = root.TryGetProperty("meta", out var meta)
                            && meta.TryGetProperty("correlationId", out var cid)
            ? cid.GetString() ?? ""
            : "";
        var actionVersion = root.TryGetProperty("meta", out meta)
                            && meta.TryGetProperty("actionVersion", out var av) && av.TryGetInt32(out var v)
            ? v
            : 0;

        return Ok(outcome ?? "ok", result, correlationId, actionVersion);
    }

    public static IResult DomainError(JsonElement envelope, string correlationId, int? actionVersion)
    {
        var code = envelope.TryGetProperty("code", out var cd) ? cd.GetString() : null;
        code = string.IsNullOrEmpty(code) ? "internal.error" : code;

        var message = envelope.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg
            ? msg
            : "error";

        return Error(code, message, false, correlationId, actionVersion, ErrorMapping.ToHttpCode(code));
    }
}
