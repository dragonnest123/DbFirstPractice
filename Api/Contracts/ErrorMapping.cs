namespace Api.Contracts;

public static class ErrorMapping
{
    public static int ToHttpCode(string code) => code switch
    {
        "access.denied" => 403,
        "action.not_found" => 404,
        "operation.not_found" => 404,
        "idempotency.conflict" => 409,
        "idempotency.required" => 400,
        "payload.invalid" => 422,
        "internal.error" => 500,
        _ => 422
    };
}