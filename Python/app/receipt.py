from __future__ import annotations

import hashlib
import hmac
import json

REQUIRED_LEGACY_FIELDS = {
    "providerPaymentId",
    "operationId",
    "result",
    "message",
    "occurredAt",
}

_TEXT_FIELDS = ("providerPaymentId", "operationId", "occurredAt")


def validate_legacy(body: dict) -> None:
    if not isinstance(body, dict):
        raise ValueError("legacy callback must be an object")
    if set(body) != REQUIRED_LEGACY_FIELDS:
        raise ValueError("unknown or missing legacy fields")
    for field in _TEXT_FIELDS:
        value = body.get(field)
        if (
            not isinstance(value, str)
            or not value
            or len(value) > 128
            or "\r" in value
            or "\n" in value
        ):
            raise ValueError(f"invalid legacy {field}")
    if body.get("result") not in ("COMPLETED", "REJECTED"):
        raise ValueError("invalid legacy result")
    message = body.get("message")
    if (
        not isinstance(message, str)
        or len(message) > 500
        or "\r" in message
        or "\n" in message
    ):
        raise ValueError("invalid legacy message")


def normalize_receipt(legacy: dict) -> dict:
    validate_legacy(legacy)
    return {
        "externalRequestId": legacy["operationId"],
        "messageId": legacy["providerPaymentId"],
        "occurredAt": legacy["occurredAt"],
        "outcome": legacy["result"],
        "providerPaymentId": legacy["providerPaymentId"],
        "version": 1,
    }


def canonical_bytes(receipt: dict) -> bytes:
    return json.dumps(
        receipt,
        ensure_ascii=False,
        separators=(",", ":"),
        sort_keys=True,
    ).encode("utf-8")


def sign(secret: str, body: bytes) -> str:
    digest = hmac.new(secret.encode("utf-8"), body, hashlib.sha256).hexdigest()
    return f"v1={digest}"