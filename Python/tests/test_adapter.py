from __future__ import annotations

import json
import threading
import urllib.error
import urllib.request
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from app.adapter import _build_adapter
from app.config import AdapterConfig

LEGACY = {
    "providerPaymentId": "provider-123",
    "operationId": "external-123",
    "result": "COMPLETED",
    "message": "Payment completed",
    "occurredAt": "2026-09-04T12:00:00Z",
}


class Upstream(BaseHTTPRequestHandler):
    status = 200
    body = b'{"status":"ok","outcome":"RECEIVED","result":{"state":"RECEIVED"}}'

    def log_message(self, *args):  # noqa: ANN002, ANN003
        return

    def do_POST(self) -> None:
        self.send_response(self.status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(self.body)))
        self.end_headers()
        self.wfile.write(self.body)


def _start_adapter(receipt_api_url: str) -> ThreadingHTTPServer:
    cfg = AdapterConfig(
        capability="cap1",
        token="jwt-token",
        hmac_secret="hmac-secret",
        receipt_api_url=receipt_api_url,
        host="127.0.0.1",
        port=0,
        api_timeout=2.0,
    )
    server = ThreadingHTTPServer(("127.0.0.1", 0), _build_adapter(cfg))
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def _start_upstream() -> ThreadingHTTPServer:
    server = ThreadingHTTPServer(("127.0.0.1", 0), Upstream)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server


def _post(url: str, body: bytes):
    request = urllib.request.Request(
        url, data=body, method="POST", headers={"Content-Type": "application/json"}
    )
    try:
        with urllib.request.urlopen(request, timeout=5) as response:
            return response.status, response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.read()


def _legacy_bytes() -> bytes:
    return json.dumps(LEGACY, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def test_adapter_passes_through_upstream_2xx() -> None:
    upstream = _start_upstream()
    adapter = _start_adapter(f"http://127.0.0.1:{upstream.server_address[1]}/api/receipt/accept")
    try:
        status, response = _post(
            f"http://127.0.0.1:{adapter.server_address[1]}/callbacks/provider-v02/cap1",
            _legacy_bytes(),
        )
        assert status == 200
        assert json.loads(response)["outcome"] == "RECEIVED"
    finally:
        adapter.shutdown()
        upstream.shutdown()


def test_adapter_passes_through_upstream_4xx() -> None:
    upstream = _start_upstream()
    upstream_body = b'{"status":"error","code":"idempotency.conflict"}'
    Upstream.status = 409
    Upstream.body = upstream_body
    adapter = _start_adapter(f"http://127.0.0.1:{upstream.server_address[1]}/api/receipt/accept")
    try:
        status, response = _post(
            f"http://127.0.0.1:{adapter.server_address[1]}/callbacks/provider-v02/cap1",
            _legacy_bytes(),
        )
        assert status == 409
        assert json.loads(response)["code"] == "idempotency.conflict"
    finally:
        adapter.shutdown()
        upstream.shutdown()
        Upstream.status = 200
        Upstream.body = b'{"status":"ok","outcome":"RECEIVED","result":{"state":"RECEIVED"}}'


def test_adapter_returns_503_when_upstream_unavailable() -> None:
    adapter = _start_adapter("http://127.0.0.1:1/api/receipt/accept")
    try:
        status, response = _post(
            f"http://127.0.0.1:{adapter.server_address[1]}/callbacks/provider-v02/cap1",
            _legacy_bytes(),
        )
        assert status == 503
        assert json.loads(response)["code"] == "dependency.unavailable"
    finally:
        adapter.shutdown()


def test_adapter_wrong_capability_returns_404() -> None:
    adapter = _start_adapter("http://127.0.0.1:1/api/receipt/accept")
    try:
        status, response = _post(
            f"http://127.0.0.1:{adapter.server_address[1]}/callbacks/provider-v02/wrong",
            _legacy_bytes(),
        )
        assert status == 404
        assert response == b""
    finally:
        adapter.shutdown()