using System.Security.Cryptography;
using System.Text;
using Api.Contracts.Dto;
using Api.Services;
using Api.Utils;
using Shared.Models;
using Shared.Services;
using Shared.Utils;

namespace Api.Endpoints;

public static class ActionEndpoint
{
    public static async Task<IResult> Handle(
        HttpContext http,
        string module,
        string action,
        JwtService jwt,
        ActionCatalogService actionCatalogService,
        ActionInvoker invoker,
        SignatureVerifier signatureVerifier)
    {
        var correlationId = Guid.NewGuid().ToString();

        if (!IdentifierUtil.IsValidSqlIdentifier(module) || !IdentifierUtil.IsValidSqlIdentifier(action))
            return Envelope.Error("request.invalid", "invalid route", false, correlationId, null, 400);

        if (!HttpUtil.TryParseVersion(http.Request, out var explicitVersion))
            return Envelope.Error("request.invalid", "invalid version header", false, correlationId, null, 400);

        if (!jwt.TryValidate(http.Request.Headers.Authorization.ToString(), out var claims)
            || !JwtService.TryGetAuthContext(claims, out var auth))
            return Envelope.Error("auth.invalid", "invalid token", false, correlationId, explicitVersion, 401);

        var bodyBytes = await HttpUtil.ReadBodyBytesAsync(http.Request);

        var signatureHeader = http.Request.Headers.TryGetValue("X-Provider-Signature", out var sig)
            ? sig.ToString()
            : "";

        if (!signatureVerifier.TryVerify(bodyBytes, signatureHeader, out var signatureVersion))
            return Envelope.Error("signature.invalid", "invalid signature", false, correlationId, explicitVersion, 401);

        var payload = Encoding.UTF8.GetString(bodyBytes);
        if (string.IsNullOrWhiteSpace(payload))
            payload = "{}";

        if (!ValidationUtil.IsValidJson(payload))
            return Envelope.Error("request.invalid", "invalid json", false, correlationId, null, 400);

        ActionManifest? entry;
        try
        {
            entry = await actionCatalogService.GetOrDefault(module, action, explicitVersion);
        }
        catch
        {
            return Envelope.Error("dependency.unavailable", "db unavailable", true, correlationId, explicitVersion, 503);
        }

        if (entry is null)
            return Envelope.Error("action.not_found", "unknown action", false, correlationId, explicitVersion, 404);

        if (!entry.HasRequiredPolicy(auth.Scopes))
            return Envelope.Error("access.denied", "insufficient scope", false, correlationId, entry.Version, 403);

        var idempotencyKey = http.Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.ToString() : "";
        if (idempotencyKey.Length > 128)
            return Envelope.Error("request.invalid", "invalid idempotency key", false, correlationId, entry.Version, 400);
        if (entry.IdempotencyMode == "required" && string.IsNullOrEmpty(idempotencyKey))
            return Envelope.Error("idempotency.required", "missing key", false, correlationId, entry.Version, 400);

        if (!ValidationUtil.IsValid(entry.RequestSchema, payload))
            return Envelope.Error("payload.invalid", "payload does not match schema", false, correlationId, entry.Version, 422);

        var requestId = string.IsNullOrEmpty(idempotencyKey) ? correlationId : idempotencyKey;
        var scopeKey = IdempotencyService.BuildScopeKey(entry, auth.Principal, auth.Consumer, module, action);
        var state = RequestState.Build(
            module,
            action,
            correlationId,
            requestId,
            auth.Principal,
            auth.Consumer,
            auth.Scopes,
            payload,
            entry,
            explicitVersion,
            actionCatalogService.ConnectionString,
            idempotencyKey,
            scopeKey,
            signatureVerified: signatureHeader.Length > 0,
            signatureVersion: signatureVersion,
            bodySha256: Convert.ToHexString(SHA256.HashData(bodyBytes)).ToLowerInvariant());

        return await invoker.InvokeAsync(state, entry, http.RequestAborted);
    }
}