from __future__ import annotations

import json
import re
import sys
import time
from typing import Any, Mapping

REDACTED = "[redacted]"

# Fields that must never reach a log sink, whatever the call site passes.
FORBIDDEN_FIELDS = frozenset(
    {
        "authorization",
        "body",
        "callback",
        "callbackBody",
        "callback_body",
        "capability",
        "credential",
        "credentials",
        "hmac",
        "password",
        "payload",
        "providerMessage",
        "provider_message",
        "raw",
        "receipt",
        "receiptBody",
        "receipt_body",
        "reason",
        "secret",
        "signature",
        "token",
    }
)

_JWT = re.compile(r"\beyJ[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\.[A-Za-z0-9_-]{4,}\b")


def _scrub(value: str) -> str:
    return _JWT.sub(REDACTED, value)


def _flatten(value: Any) -> Any:
    if value is None or isinstance(value, (bool, int, float)):
        return value
    if isinstance(value, str):
        return _scrub(value)
    if isinstance(value, Mapping):
        return {
            str(key): (REDACTED if str(key) in FORBIDDEN_FIELDS else _flatten(item))
            for key, item in value.items()
        }
    if isinstance(value, (list, tuple)):
        return [_flatten(item) for item in value]
    return _scrub(str(value))


def log(event: str, **fields: Any) -> None:
    """Writes one JSON log line. Payload-like fields are dropped, not truncated."""
    record = {"ts": round(time.time(), 3), "event": event}
    for key, value in fields.items():
        if key in FORBIDDEN_FIELDS:
            record[key] = REDACTED
        else:
            record[key] = _flatten(value)
    line = json.dumps(record, ensure_ascii=False, separators=(",", ":"))
    print(line, file=sys.stdout, flush=True)
