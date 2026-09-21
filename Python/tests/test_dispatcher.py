from __future__ import annotations

import json

from app.dispatcher import build_provider_request

CORRELATION = "11111111-1111-1111-1111-111111111111"


def test_provider_body_and_headers_are_stable_across_retries() -> None:
    first = build_provider_request("ext-1", CORRELATION, "1000.00", "RUB")
    second = build_provider_request("ext-1", CORRELATION, "1000.00", "RUB")

    assert first == second
    body, headers = first
    payload = json.loads(body)
    assert payload == {"operationId": "ext-1", "amount": "1000.00", "currency": "RUB"}
    assert headers["Idempotency-Key"] == "ext-1"
    assert headers["X-Correlation-ID"] == CORRELATION
    assert headers["Content-Type"] == "application/json"


def test_provider_request_preserves_exact_legacy_body_bytes() -> None:
    body, _ = build_provider_request("ext-1", CORRELATION, "1000.00", "RUB")
    expected = json.dumps(
        {"operationId": "ext-1", "amount": "1000.00", "currency": "RUB"},
        ensure_ascii=False,
        separators=(",", ":"),
    ).encode("utf-8")
    assert body == expected


def test_distinct_deliveries_have_distinct_idempotency_keys() -> None:
    first = build_provider_request("ext-1", CORRELATION, "1000.00", "RUB")
    second = build_provider_request("ext-2", "22222222-2222-2222-2222-222222222222", "2000.00", "RUB")
    assert first[1]["Idempotency-Key"] != second[1]["Idempotency-Key"]
    assert first[1]["X-Correlation-ID"] != second[1]["X-Correlation-ID"]