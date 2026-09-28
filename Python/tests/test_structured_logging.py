from __future__ import annotations

import json

from app import structured_logging


def _record(capsys) -> dict:
    return json.loads(capsys.readouterr().out.strip())


def test_event_and_timestamp_are_always_present(capsys) -> None:
    structured_logging.log("outbox.attempt", outcome="delivered")
    record = _record(capsys)
    assert record["event"] == "outbox.attempt"
    assert isinstance(record["ts"], float)
    assert record["outcome"] == "delivered"


def test_payload_like_fields_are_redacted(capsys) -> None:
    structured_logging.log(
        "provider_callback.forwarded",
        body="Public deterministic rejection",
        reason="Rejected by limit rule",
        receipt={"messageId": "m-1"},
        providerStatus=200,
    )
    record = _record(capsys)
    assert record["body"] == structured_logging.REDACTED
    assert record["reason"] == structured_logging.REDACTED
    assert record["receipt"] == structured_logging.REDACTED
    assert record["providerStatus"] == 200


def test_nested_payload_values_are_scrubbed(capsys) -> None:
    structured_logging.log("outbox.attempt", context={"note": "Payment completed"})
    record = _record(capsys)
    assert record["context"]["note"] == "Payment completed"


def test_jwt_shaped_values_are_scrubbed(capsys) -> None:
    token = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.c2lnbmF0dXJl"
    structured_logging.log("adapter.forwarded", note=token)
    record = _record(capsys)
    assert token not in json.dumps(record)
    assert record["note"] == structured_logging.REDACTED
