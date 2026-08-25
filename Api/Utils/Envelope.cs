using System.Text.Json;

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
}
