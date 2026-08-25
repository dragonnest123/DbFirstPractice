using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Api.Services;
using Api.Utils;
using Npgsql;

namespace Api.Endpoints;

public static partial class ActionEndpoints
{
    private static readonly Regex SqlIdentifier = MyRegex();

    public static void Map(WebApplication app) =>
        app.MapPost("/api/{module}/{action}", HandleAsync);

    private static async Task<IResult> HandleAsync(
        HttpContext http,
        string module,
        string action,
        JwtValidator jwt,
        CatalogService catalogService,
        DispatchService dispatchService,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger("Api.Endpoints.ActionEndpoints");
        var correlationId = Guid.NewGuid().ToString();
        var payload = await ReadBodyAsync(http.Request);

        if (!IsValidJson(payload))
            return Envelope.Error("request.invalid", "invalid json", false, correlationId, null, 400);

        if (!IsValidSqlIdentifier(module) || !IsValidSqlIdentifier(action))
            return Envelope.Error("request.invalid", "invalid route", false, correlationId, null, 400);

        if (!TryParseVersion(http.Request, out var explicitVersion))
            return Envelope.Error("request.invalid", "invalid version header", false, correlationId, null, 400);

        if (!jwt.TryValidate(http.Request.Headers.Authorization.ToString(), out var claims))
            return Envelope.Error("auth.invalid", "invalid token", false, correlationId, explicitVersion, 401);

        var principal = claims.GetProperty("sub").GetString()!;
        var consumer = claims.GetProperty("consumer").GetString()!;
        var scopes = claims.GetProperty("scope").GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        CatalogEntry? entry;
        try
        {
            entry = await catalogService.ResolveAsync(module, action, explicitVersion);
        }
        catch
        {
            return Envelope.Error("dependency.unavailable", "db unavailable", true, correlationId, explicitVersion, 503);
        }

        if (entry is null)
            return Envelope.Error("action.not_found", "unknown action", false, correlationId, explicitVersion, 404);

        if (!HasRequiredPolicy(entry, scopes))
            return Envelope.Error("access.denied", "insufficient scope", false, correlationId, entry.Version, 403);

        var idempotencyKey = http.Request.Headers.TryGetValue("Idempotency-Key", out var values) ? values.ToString() : "";
        if (entry.IdempotencyMode == "required" && string.IsNullOrEmpty(idempotencyKey))
            return Envelope.Error("idempotency.required", "missing key", false, correlationId, entry.Version, 400);

        if (!SchemaValidator.IsValid(entry.RequestSchema, payload))
            return Envelope.Error("payload.invalid", "payload does not match schema", false, correlationId, entry.Version, 422);

        var requestId = string.IsNullOrEmpty(idempotencyKey) ? correlationId : idempotencyKey;
        var scopeKey = entry.IdempotencyScope switch
        {
            "principal_action" => $"{principal}:{module}.{action}",
            "consumer_action" => $"{consumer}:{module}.{action}",
            "global_action" => $"{module}.{action}",
            _ => null
        };
        var state = new RequestState(
            module,
            action,
            correlationId,
            requestId,
            principal,
            HashUtil.Sha256Hex(payload),
            JsonSerializer.Serialize(new
            {
                principal,
                consumer,
                scopes,
                correlationId,
                requestId,
                deadline = DateTime.UtcNow.AddMilliseconds(entry.TimeoutMs).ToString("o")
            }),
            payload,
            explicitVersion,
            entry.Version,
            entry.TimeoutMs,
            catalogService.ConnectionString,
            entry.IdempotencyMode != "none" && !string.IsNullOrEmpty(idempotencyKey) ? scopeKey : null);

        return await InvokeAsync(state, entry, dispatchService, logger);
    }

    private static async Task<IResult> InvokeAsync(
        RequestState s,
        CatalogEntry entry,
        DispatchService dispatchService,
        ILogger logger)
    {
        try
        {
            await using var conn = new NpgsqlConnection(s.ConnectionString);
            await conn.OpenAsync();
            
            await using var tx = await conn.BeginTransactionAsync();
            var cts = new CancellationTokenSource(s.TimeoutMs);

            if (s.IdempotencyScopeKey is not null)
            {
                try
                {
                    await using var claim = new NpgsqlCommand(
                        "INSERT INTO api.idempotency_store(scope_key, request_id, payload_hash, response) VALUES(@k,@r,@h,NULL)",
                        conn, tx);
                    claim.Parameters.AddWithValue("k", s.IdempotencyScopeKey);
                    claim.Parameters.AddWithValue("r", s.RequestId);
                    claim.Parameters.AddWithValue("h", s.PayloadHash);
                    await claim.ExecuteNonQueryAsync(cts.Token);
                }
                catch (PostgresException ex) when (ex.SqlState == "23505")
                {
                    await tx.RollbackAsync(cts.Token);
                    var existing = await ReadIdempotencyAsync(conn, null, s.IdempotencyScopeKey, s.RequestId);
                    if (existing is null)
                        return Envelope.Error("idempotency.conflict", "same key different payload", false, s.CorrelationId, s.Version, 409);
                    return existing.Value.Hash == s.PayloadHash && existing.Value.Response.Length > 0
                        ? ReplayResult(existing.Value.Response)
                        : Envelope.Error("idempotency.conflict", "same key different payload", false, s.CorrelationId, s.Version, 409);
                }
            }

            string invokeJson;
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT api.invoke(@m,@a,@v,@ctx::jsonb,@pay::jsonb)::text",
                    conn, tx);
                cmd.CommandTimeout = Math.Max(1, s.TimeoutMs / 1000 + 2);
                cmd.Parameters.AddWithValue("m", s.Module);
                cmd.Parameters.AddWithValue("a", s.Action);
                cmd.Parameters.AddWithValue("v", s.ExplicitVersion.HasValue ? s.ExplicitVersion.Value : DBNull.Value);
                cmd.Parameters.AddWithValue("ctx", s.ContextJson);
                cmd.Parameters.AddWithValue("pay", s.Payload);
                invokeJson = (await cmd.ExecuteScalarAsync(cts.Token))?.ToString() ?? "";
            }
            catch (OperationCanceledException)
            {
                await tx.RollbackAsync(cts.Token);
                return Envelope.Error("action.timeout", "timeout", true, s.CorrelationId, s.Version, 504);
            }
            catch (NpgsqlException)
            {
                await tx.RollbackAsync(cts.Token);
                return Envelope.Error("dependency.unavailable", "db unavailable", true, s.CorrelationId, s.Version, 503);
            }

            JsonDocument envelope;
            try
            {
                envelope = JsonDocument.Parse(invokeJson);
            }
            catch
            {
                return await FailContractAsync(tx, dispatchService, s, "invalid envelope", null);
            }

            var root = envelope.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() == "error")
                return await FailDomainAsync(tx, dispatchService, s, root);

            var outcome = root.TryGetProperty("outcome", out var oc) ? oc.GetString() : null;
            if (outcome is null || entry.Outcomes.EnumerateArray().All(x => x.GetString() != outcome))
                return await FailContractAsync(tx, dispatchService, s, "unknown outcome", null);

            if (!root.TryGetProperty("result", out var result))
                return await FailContractAsync(tx, dispatchService, s, "missing result", outcome);

            if (!SchemaValidator.IsValidResult(entry.ResponseSchema, result))
                return await FailContractAsync(tx, dispatchService, s, "result schema violation", outcome);

            try
            {
                await using var dcmd = new NpgsqlCommand(
                    "INSERT INTO api.action_dispatches(correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome) VALUES(@c,@r,@m,@a,@v,@p,@h,'OK',@o)",
                    conn, tx);
                dcmd.Parameters.AddWithValue("c", Guid.Parse(s.CorrelationId));
                dcmd.Parameters.AddWithValue("r", s.RequestId);
                dcmd.Parameters.AddWithValue("m", s.Module);
                dcmd.Parameters.AddWithValue("a", s.Action);
                dcmd.Parameters.AddWithValue("v", s.Version);
                dcmd.Parameters.AddWithValue("p", s.Principal);
                dcmd.Parameters.AddWithValue("h", s.PayloadHash);
                dcmd.Parameters.AddWithValue("o", outcome!);
                await dcmd.ExecuteNonQueryAsync(cts.Token);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to record OK dispatchService for correlation {CorrelationId}", s.CorrelationId);
            }

            if (s.IdempotencyScopeKey is not null)
            {
                try
                {
                    await using var store = new NpgsqlCommand(
                        "UPDATE api.idempotency_store SET response = @resp::jsonb WHERE scope_key=@k AND request_id=@r",
                        conn, tx);
                    store.Parameters.AddWithValue("resp", invokeJson);
                    store.Parameters.AddWithValue("k", s.IdempotencyScopeKey);
                    store.Parameters.AddWithValue("r", s.RequestId);
                    await store.ExecuteNonQueryAsync(cts.Token);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to persist idempotency response for correlation {CorrelationId}", s.CorrelationId);
                }
            }

            await tx.CommitAsync(cts.Token);
            return Envelope.Ok(outcome!, result, s.CorrelationId, s.Version);
        }
        catch (NpgsqlException)
        {
            return Envelope.Error("dependency.unavailable", "db unavailable", true, s.CorrelationId, s.Version, 503);
        }
    }

    private static async Task<IResult> FailDomainAsync(
        NpgsqlTransaction tx, DispatchService dispatchService, RequestState s, JsonElement root)
    {
        var code = root.TryGetProperty("code", out var cd) ? cd.GetString() : null;
        code = string.IsNullOrEmpty(code) ? "internal.error" : code;
        var message = root.TryGetProperty("message", out var m) && m.GetString() is { Length: > 0 } msg ? msg : "error";
        var httpCode = code switch
        {
            "access.denied" => 403,
            "action.not_found" => 404,
            "operation.not_found" => 404,
            "idempotency.conflict" => 409,
            "idempotency.required" => 400,
            "payload.invalid" => 422,
            _ => 500
        };
        await tx.RollbackAsync();
        await dispatchService.LogAsync(s.CorrelationId, s.RequestId, s.Module, s.Action, s.Version, s.Principal, s.PayloadHash, "ERROR", null);
        return Envelope.Error(code, message, false, s.CorrelationId, s.Version, httpCode);
    }

    private static async Task<IResult> FailContractAsync(
        NpgsqlTransaction tx, DispatchService dispatchService, RequestState s, string message, string? outcome)
    {
        await tx.RollbackAsync();
        await dispatchService.LogAsync(s.CorrelationId, s.RequestId, s.Module, s.Action, s.Version, s.Principal, s.PayloadHash, "ERROR", outcome);
        return Envelope.Error("action.contract_violation", message, false, s.CorrelationId, s.Version, 500);
    }

    private static async Task<string> ReadBodyAsync(HttpRequest request)
    {
        using var reader = new StreamReader(request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(body) ? "{}" : body;
    }

    private static async Task<(string Hash, string Response)?> ReadIdempotencyAsync(
        NpgsqlConnection conn, NpgsqlTransaction? tx, string scopeKey, string requestId)
    {
        await using var cmd = new NpgsqlCommand(
            "SELECT payload_hash, response FROM api.idempotency_store WHERE scope_key=@k AND request_id=@r",
            conn, tx);
        cmd.Parameters.AddWithValue("k", scopeKey);
        cmd.Parameters.AddWithValue("r", requestId);
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        var hash = reader.GetString(0);
        var response = reader.IsDBNull(1) ? null : reader.GetString(1);
        return (hash, response ?? "");
    }

    private static IResult ReplayResult(string responseJson)
    {
        using var doc = JsonDocument.Parse(responseJson);
        var root = doc.RootElement;
        var outcome = root.TryGetProperty("outcome", out var oc) ? oc.GetString() : null;
        var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
        var correlationId = root.TryGetProperty("meta", out var meta) &&
                            meta.TryGetProperty("correlationId", out var cid)
            ? cid.GetString() ?? ""
            : "";
        var actionVersion = root.TryGetProperty("meta", out meta) &&
                            meta.TryGetProperty("actionVersion", out var av) && av.TryGetInt32(out var v)
            ? v
            : 0;
        return Envelope.Ok(outcome ?? "ok", result, correlationId, actionVersion);
    }

    private static bool IsValidJson(string json)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidSqlIdentifier(string value) => SqlIdentifier.IsMatch(value);

    private static bool TryParseVersion(HttpRequest request, out int? version)
    {
        version = null;
        if (!request.Headers.TryGetValue("X-Action-Version", out var values)) return true;
        var raw = values.ToString();
        if (!int.TryParse(raw, out var v) || v < 1) return false;
        version = v;
        return true;
    }

    private static bool HasRequiredPolicy(CatalogEntry entry, string[] scopes) 
        => entry.RequiredPolicy.EnumerateArray().Select(x => x.GetString()!).All(need => scopes.Contains(need));

    private sealed record RequestState(
        string Module,
        string Action,
        string CorrelationId,
        string RequestId,
        string Principal,
        string PayloadHash,
        string ContextJson,
        string Payload,
        int? ExplicitVersion,
        int Version,
        int TimeoutMs,
        string ConnectionString,
        string? IdempotencyScopeKey);

    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}