using System.Text.Json;
using Api.Dto;
using Api.Utils;
using Npgsql;
using Shared.Models;

namespace Api.Services;

public sealed class ActionInvoker
{
    private static readonly TimeSpan RollbackTimeout = TimeSpan.FromSeconds(5);

    private readonly IdempotencyService _idempotency;
    private readonly DispatchService _dispatch;

    public ActionInvoker(IdempotencyService idempotency, DispatchService dispatch)
    {
        _idempotency = idempotency;
        _dispatch = dispatch;
    }

    public async Task<IResult> InvokeAsync(RequestState s, ActionManifest entry, CancellationToken requestAborted)
    {
        await using var conn = new NpgsqlConnection(s.ConnectionString);
        NpgsqlTransaction? tx = null;
        using var timeoutCts = new CancellationTokenSource(s.TimeoutMs);
        using var invocationCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, requestAborted);
        using var rollbackCts = new CancellationTokenSource(RollbackTimeout);

        try
        {
            await conn.OpenAsync();

            tx = await conn.BeginTransactionAsync();

            if (s.IdempotencyScopeKey is not null)
            {
                var guard = await _idempotency.ClaimOrReplayAsync(conn, tx, s, invocationCts.Token);
                if (guard is not null)
                    return guard;
            }

            var invokeJson = await ExecuteInvokeAsync(conn, tx, s, invocationCts.Token);

            JsonDocument envelope;
            try
            {
                envelope = JsonDocument.Parse(invokeJson);
            }
            catch
            {
                return await HandleContractViolationAsync(tx, s, "invalid envelope", null);
            }

            var root = envelope.RootElement;
            if (root.TryGetProperty("status", out var st) && st.GetString() == "error")
                return await HandleDomainErrorAsync(tx, s, root);

            var outcome = root.TryGetProperty("outcome", out var oc) ? oc.GetString() : null;
            if (outcome is null || entry.Outcomes.All(x => x != outcome))
                return await HandleContractViolationAsync(tx, s, "unknown outcome", null);

            if (!root.TryGetProperty("result", out var result))
                return await HandleContractViolationAsync(tx, s, "missing result", outcome);

            if (!ValidationUtil.IsValidResult(entry.ResponseSchema, result))
                return await HandleContractViolationAsync(tx, s, "result schema violation", outcome);

            await _dispatch.LogAsync(
                s.CorrelationId,
                s.RequestId,
                s.Module,
                s.Action,
                s.Version,
                s.Principal,
                s.PayloadHash,
                "OK",
                outcome,
                conn,
                tx,
                invocationCts.Token);

            if (s.IdempotencyScopeKey is not null)
                await _idempotency.StoreResponseAsync(conn, tx, s.IdempotencyScopeKey, s.RequestId, invokeJson, invocationCts.Token);

            await tx.CommitAsync(invocationCts.Token);
            return Envelope.Ok(outcome!, result, s.CorrelationId, s.Version);
        }
        catch (OperationCanceledException)
        {
            if (tx is not null)
                await RollbackBestEffortAsync(tx, rollbackCts.Token);
            return Envelope.Error("action.timeout", "timeout", true, s.CorrelationId, s.Version, 504);
        }
        catch (NpgsqlException)
        {
            if (tx is not null)
                await RollbackBestEffortAsync(tx, rollbackCts.Token);
            return Envelope.Error("dependency.unavailable", "db unavailable", true, s.CorrelationId, s.Version, 503);
        }
    }

    private static async Task<string> ExecuteInvokeAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, RequestState s, CancellationToken ct)
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

        return (await cmd.ExecuteScalarAsync(ct))?.ToString() ?? "";
    }

    private static async Task RollbackBestEffortAsync(NpgsqlTransaction tx, CancellationToken ct)
    {
        try
        {
            await tx.RollbackAsync(ct);
        }
        catch
        {
        }
    }

    private async Task<IResult> HandleDomainErrorAsync(NpgsqlTransaction tx, RequestState s, JsonElement root)
    {
        await tx.RollbackAsync();
        await _dispatch.LogAsync(
            s.CorrelationId,
            s.RequestId,
            s.Module,
            s.Action,
            s.Version,
            s.Principal,
            s.PayloadHash,
            "ERROR",
            null);

        return Envelope.DomainError(root, s.CorrelationId, s.Version);
    }

    private async Task<IResult> HandleContractViolationAsync(NpgsqlTransaction tx, RequestState s, string message, string? outcome)
    {
        await tx.RollbackAsync();
        await _dispatch.LogAsync(
            s.CorrelationId,
            s.RequestId,
            s.Module,
            s.Action,
            s.Version,
            s.Principal,
            s.PayloadHash,
            "ERROR",
            outcome);

        return Envelope.Error("action.contract_violation", message, false, s.CorrelationId, s.Version, 500);
    }
}