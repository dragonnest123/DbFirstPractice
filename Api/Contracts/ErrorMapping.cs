namespace Api.Contracts;

public static class ErrorMapping
{
    public static int ToHttpCode(string code) => code switch
    {
        "access.denied" => 403,
        "action.not_found" => 404,
        "operation.not_found" => 404,
        "receipt.signature_required" => 403,
        "signature.invalid" => 401,
        "receipt.external_request_not_found" => 422,
        "idempotency.conflict" => 409,
        "workflow.decision_conflict" => 409,
        "idempotency.required" => 400,
        "payload.invalid" => 422,
        "internal.error" => 500,
        _ => 422
    };
}