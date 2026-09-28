from __future__ import annotations

import json
import time

import psycopg

from app import observability, structured_logging
from app.config import DispatcherConfig, dispatcher_config
from app.db import connect
from app.failpoints import FailpointController
from app.http_client import HttpError, post_json
from app.metrics import Registry

CLAIM_FAILPOINT = "after_outbox_claim"
RESPONSE_FAILPOINT = "after_provider_response"


def _provider_body(external_request_id: str, amount: str, currency: str) -> bytes:
    return json.dumps(
        {
            "operationId": external_request_id,
            "amount": amount,
            "currency": currency,
        },
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")


def build_provider_request(
    external_request_id: str,
    correlation_id: str,
    amount: str,
    currency: str,
) -> tuple[bytes, dict[str, str]]:
    """Builds the exact provider request. Deterministic for a given delivery row,
    so every retry preserves the same key, body and correlation identifier."""
    body = _provider_body(external_request_id, amount, currency)
    headers = {
        "Content-Type": "application/json",
        "Idempotency-Key": external_request_id,
        "X-Correlation-ID": str(correlation_id),
    }
    return body, headers


def _succeed(
    conn: psycopg.Connection,
    cfg: DispatcherConfig,
    outbox_id: str,
    lease_version: int,
    provider_payment_id: str,
) -> str | None:
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.succeed_outbox(%s, %s, %s, %s)",
            (outbox_id, cfg.owner, lease_version, provider_payment_id),
        )
        result = cursor.fetchone()
    if result and isinstance(result[0], str):
        return None
    return "lease.conflict"


def _fail(
    conn: psycopg.Connection,
    cfg: DispatcherConfig,
    outbox_id: str,
    lease_version: int,
    error_code: str,
) -> str | None:
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.fail_outbox(%s, %s, %s, %s)",
            (outbox_id, cfg.owner, lease_version, error_code),
        )
        result = cursor.fetchone()
    if result and isinstance(result[0], str):
        return None
    return "lease.conflict"


def classify_response(status: int, body: bytes) -> tuple[str, str | None]:
    """Returns (error_code, provider_payment_id). None code means success."""
    if status == 202:
        try:
            payload = json.loads(body.decode("utf-8"))
        except (UnicodeDecodeError, json.JSONDecodeError):
            return "response.invalid.terminal", None
        if (
            not isinstance(payload, dict)
            or set(payload) != {"providerPaymentId", "status"}
            or payload.get("status") != "ACCEPTED"
            or not isinstance(payload.get("providerPaymentId"), str)
            or not payload.get("providerPaymentId")
        ):
            return "response.invalid.terminal", None
        return None, payload["providerPaymentId"]
    if status in (408, 429) or 500 <= status <= 599:
        return f"http.{status}.retryable", None
    return f"http.{status}.terminal", None


def _attempt(
    conn: psycopg.Connection,
    cfg: DispatcherConfig,
    row: tuple,
    failpoints: FailpointController,
    registry: Registry,
) -> None:
    (
        outbox_id,
        lease_version,
        external_request_id,
        correlation_id,
        amount,
        currency,
    ) = row
    body, headers = build_provider_request(
        external_request_id, correlation_id, amount, currency
    )
    try:
        status, response_body, _ = post_json(
            f"{cfg.provider_url.rstrip('/')}/payments", body, headers, cfg.provider_timeout
        )
    except HttpError as error:
        registry.increment("outbox_dispatch_attempts", labels={"outcome": "transport_error"})
        conflict = _fail(conn, cfg, outbox_id, lease_version, "transport.error.retryable")
        if conflict:
            registry.increment("outbox_lease_conflicts")
        structured_logging.log(
            "outbox.attempt",
            outcome="retryable",
            errorCode="transport.error.retryable",
            conflict=conflict,
        )
        return

    failpoints.reach(RESPONSE_FAILPOINT, outbox_id)

    error_code, provider_payment_id = classify_response(status, response_body)
    if error_code is None:
        conflict = _succeed(conn, cfg, outbox_id, lease_version, provider_payment_id)
        if conflict:
            registry.increment("outbox_lease_conflicts")
            registry.increment("outbox_dispatch_attempts", labels={"outcome": "fenced"})
        else:
            registry.increment("outbox_dispatch_attempts", labels={"outcome": "delivered"})
        structured_logging.log(
            "outbox.attempt",
            outcome="delivered",
            providerStatus=status,
            conflict=conflict,
        )
        return

    conflict = _fail(conn, cfg, outbox_id, lease_version, error_code)
    if conflict:
        registry.increment("outbox_lease_conflicts")
        registry.increment("outbox_dispatch_attempts", labels={"outcome": "fenced"})
    else:
        registry.increment("outbox_dispatch_attempts", labels={"outcome": "failed"})
    structured_logging.log(
        "outbox.attempt",
        outcome="failed",
        errorCode=error_code,
        providerStatus=status,
        conflict=conflict,
    )


def run_once(
    conn: psycopg.Connection,
    cfg: DispatcherConfig,
    failpoints: FailpointController | None = None,
    registry: Registry | None = None,
) -> int:
    failpoints = failpoints or FailpointController(None, False)
    registry = registry or Registry()
    with conn.cursor() as cursor:
        cursor.execute("SELECT * FROM delivery.claim_outbox(%s, %s)", (cfg.owner, cfg.claim_batch))
        rows = cursor.fetchall()
    registry.set("outbox_pending", len(rows))
    if rows:
        failpoints.reach(CLAIM_FAILPOINT, rows[0][0])
    for row in rows:
        try:
            _attempt(conn, cfg, row, failpoints, registry)
        except psycopg.Error as error:
            registry.increment("dispatcher_database_errors")
            structured_logging.log("outbox.database_error", error=type(error).__name__)
    registry.set("outbox_pending", 0)
    return len(rows)


def _database_probe(cfg: DispatcherConfig):
    def probe() -> None:
        with connect(cfg.pg) as conn:
            with conn.cursor() as cursor:
                cursor.execute("SELECT 1")
                cursor.fetchone()

    return probe


def main() -> None:
    cfg = dispatcher_config()
    failpoints = FailpointController.from_environment()
    registry = Registry()
    registry.set("process_start_time_seconds", time.time())
    observability.serve(
        registry,
        {"postgres": observability.DependencyProbe("postgres", _database_probe(cfg))},
        cfg.observability.service,
        cfg.observability.host,
        cfg.observability.port,
    )
    structured_logging.log(
        "dispatcher.started",
        owner=cfg.owner,
        applicationName=cfg.pg.appname,
        providerUrl=cfg.provider_url,
        failpoint=failpoints.describe(),
    )
    while True:
        try:
            with connect(cfg.pg) as conn:
                run_once(conn, cfg, failpoints, registry)
        except psycopg.Error as error:
            registry.increment("dispatcher_database_errors")
            structured_logging.log("dispatcher.connection_error", error=type(error).__name__)
        time.sleep(cfg.poll_interval)


if __name__ == "__main__":
    main()
