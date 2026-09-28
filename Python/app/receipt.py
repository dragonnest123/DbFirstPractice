from __future__ import annotations

import hashlib
import hmac
import json
import re
from datetime import datetime

REQUIRED_LEGACY_FIELDS = {
    "providerPaymentId",
    "operationId",
    "result",
    "message",
    "occurredAt",
}

_TEXT_FIELDS = ("providerPaymentId", "operationId", "occurredAt")

_RFC3339_UTC_RE = re.compile(r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d+)?Z$")


def _validate_occurred_at(value: str) -> None:
    if not _RFC3339_UTC_RE.fullmatch(value):
        raise ValueError("invalid legacy occurredAt")
    try:
        datetime.fromisoformat(value[:-1] + "+00:00")
    except ValueError as error:
        raise ValueError("invalid legacy occurredAt") from error


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
    _validate_occurred_at(body["occurredAt"])
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