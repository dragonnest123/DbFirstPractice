from __future__ import annotations

import json
import time

import psycopg

from app.config import DispatcherConfig, dispatcher_config
from app.db import connect
from app.http_client import HttpError, post_json


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
) -> None:
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.succeed_outbox(%s, %s, %s, %s)",
            (outbox_id, cfg.owner, lease_version, provider_payment_id),
        )
        cursor.fetchone()


def _fail(
    conn: psycopg.Connection,
    cfg: DispatcherConfig,
    outbox_id: str,
    lease_version: int,
    error_code: str,
) -> None:
    with conn.cursor() as cursor:
        cursor.execute(
            "SELECT delivery.fail_outbox(%s, %s, %s, %s)",
            (outbox_id, cfg.owner, lease_version, error_code),
        )
        cursor.fetchone()


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


def _attempt(conn: psycopg.Connection, cfg: DispatcherConfig, row: tuple) -> None:
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
    except HttpError:
        _fail(conn, cfg, outbox_id, lease_version, "transport.error.retryable")
        print(
            json.dumps(
                {"ts": time.time(), "event": "dispatcher.attempt", "outcome": "retryable", "error": "transport.error.retryable"},
                separators=(",", ":"),
            ),
            flush=True,
        )
        return

    error_code, provider_payment_id = classify_response(status, response_body)
    if error_code is None:
        _succeed(conn, cfg, outbox_id, lease_version, provider_payment_id)
        print(
            json.dumps(
                {"ts": time.time(), "event": "dispatcher.attempt", "outcome": "delivered", "status": status},
                separators=(",", ":"),
            ),
            flush=True,
        )
        return
    _fail(conn, cfg, outbox_id, lease_version, error_code)
    print(
        json.dumps(
            {"ts": time.time(), "event": "dispatcher.attempt", "outcome": "failed", "error": error_code, "status": status},
            separators=(",", ":"),
        ),
        flush=True,
    )


def run_once(conn: psycopg.Connection, cfg: DispatcherConfig) -> int:
    with conn.cursor() as cursor:
        cursor.execute("SELECT * FROM delivery.claim_outbox(%s, %s)", (cfg.owner, cfg.claim_batch))
        rows = cursor.fetchall()
    for row in rows:
        try:
            _attempt(conn, cfg, row)
        except psycopg.Error as error:
            print(
                json.dumps(
                    {"ts": time.time(), "event": "dispatcher.db_error", "error": str(error)},
                    separators=(",", ":"),
                ),
                flush=True,
            )
    return len(rows)


def main() -> None:
    cfg = dispatcher_config()
    print(
        json.dumps(
            {"ts": time.time(), "event": "dispatcher.started", "owner": cfg.owner},
            separators=(",", ":"),
        ),
        flush=True,
    )
    while True:
        try:
            with connect(cfg.pg) as conn:
                run_once(conn, cfg)
        except psycopg.Error as error:
            print(
                json.dumps(
                    {"ts": time.time(), "event": "dispatcher.connection_error", "error": str(error)},
                    separators=(",", ":"),
                ),
                flush=True,
            )
        time.sleep(cfg.poll_interval)


if __name__ == "__main__":
    main()