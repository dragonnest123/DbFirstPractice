from __future__ import annotations

import hashlib
import hmac
import json

import pytest

from app.receipt import canonical_bytes, normalize_receipt, sign

LEGACY = {
    "providerPaymentId": "provider-123",
    "operationId": "external-123",
    "result": "COMPLETED",
    "message": "Payment completed",
    "occurredAt": "2026-09-04T12:00:00Z",
}

EXPECTED_RECEIPT = {
    "externalRequestId": "external-123",
    "messageId": "provider-123",
    "occurredAt": "2026-09-04T12:00:00Z",
    "outcome": "COMPLETED",
    "providerPaymentId": "provider-123",
    "version": 1,
}


def test_legacy_mapping_and_exact_bytes() -> None:
    receipt = normalize_receipt(LEGACY)
    assert receipt == EXPECTED_RECEIPT
    expected = json.dumps(
        receipt, ensure_ascii=False, separators=(",", ":"), sort_keys=True
    ).encode("utf-8")
    assert canonical_bytes(receipt) == expected


def test_hmac_uses_raw_utf8_secret_and_exact_body() -> None:
    secret = "s3cr3t-key"
    receipt = normalize_receipt(LEGACY)
    body = canonical_bytes(receipt)
    signature = sign(secret, body)
    expected = "v1=" + hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
    assert signature == expected

    other = json.dumps(receipt, ensure_ascii=False).encode("utf-8")
    assert sign(secret, other) != signature


def test_unknown_fields_are_rejected() -> None:
    bad = dict(LEGACY)
    bad["extra"] = "x"
    with pytest.raises(ValueError):
        normalize_receipt(bad)


def test_crlf_is_rejected_at_every_position() -> None:
    for field in ("providerPaymentId", "operationId", "occurredAt"):
        for value in ("a\rb", "a\nb"):
            bad = dict(LEGACY)
            bad[field] = value
            with pytest.raises(ValueError):
                normalize_receipt(bad)


def test_provider_payment_id_is_required_string() -> None:
    for value in ("", None, 123):
        bad = dict(LEGACY)
        bad["providerPaymentId"] = value
        with pytest.raises(ValueError):
            normalize_receipt(bad)


def test_message_is_validated_and_discarded() -> None:
    bad = dict(LEGACY)
    bad["message"] = "x" * 501
    with pytest.raises(ValueError):
        normalize_receipt(bad)

    receipt = normalize_receipt(LEGACY)
    assert "message" not in receipt


def test_occurred_at_preserved_as_raw_string() -> None:
    legacy = dict(LEGACY)
    legacy["occurredAt"] = "2026-09-04T12:00:00.123Z"
    receipt = normalize_receipt(legacy)
    assert receipt["occurredAt"] == "2026-09-04T12:00:00.123Z"