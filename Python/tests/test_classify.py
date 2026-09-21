from __future__ import annotations

import json

from app.dispatcher import classify_response


def test_202_valid_body() -> None:
    body = json.dumps({"providerPaymentId": "p-1", "status": "ACCEPTED"}).encode("utf-8")
    assert classify_response(202, body) == (None, "p-1")


def test_202_invalid_body_is_terminal() -> None:
    cases = (
        b"{}",
        b'{"providerPaymentId":"p"}',
        b'{"providerPaymentId":"p","status":"REJECTED"}',
        b'{"providerPaymentId":1,"status":"ACCEPTED"}',
        b"not-json",
        b"\xff\xfe",
    )
    for body in cases:
        assert classify_response(202, body) == ("response.invalid.terminal", None)


def test_retryable_http_statuses() -> None:
    for status in (408, 429, 500, 502, 503, 504, 599):
        assert classify_response(status, b"{}") == (f"http.{status}.retryable", None)


def test_terminal_http_statuses() -> None:
    for status in (400, 401, 403, 404, 409, 422, 418):
        assert classify_response(status, b"{}") == (f"http.{status}.terminal", None)