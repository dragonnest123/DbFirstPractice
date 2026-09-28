from __future__ import annotations

import json
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

from app import observability, structured_logging
from app.config import AdapterConfig, adapter_config
from app.http_client import HttpError, post_json
from app.metrics import Registry
from app.receipt import canonical_bytes, normalize_receipt, sign

MAX_BODY_BYTES = 64 * 1024


def _build_adapter(cfg: AdapterConfig, registry: Registry, probes: dict):
    class AdapterHandler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):  # noqa: ANN002, ANN003
            return

        def _send_empty(self, status: int) -> None:
            self.send_response(status)
            self.send_header("Content-Length", "0")
            self.end_headers()

        def _send_json(self, status: int, payload: dict) -> None:
            body = observability.json_body(payload)
            self.send_response(status)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def _send_raw(self, status: int, body: bytes, content_type: str | None) -> None:
            self.send_response(status)
            if content_type:
                self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            if body:
                self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
            resolved = observability.respond(
                self.path.split("?", 1)[0],
                registry,
                probes,
                cfg.observability.service,
            )
            if resolved is None:
                self._send_empty(404)
                return
            status, content_type, body = resolved
            self._send_raw(status, body, content_type)

        def do_POST(self) -> None:
            expected = f"/callbacks/provider-v02/{cfg.capability}"
            if self.path.split("?", 1)[0] != expected:
                registry.increment("provider_callback_rejected", labels={"reason": "unknown_path"})
                self._send_empty(404)
                return

            try:
                length = int(self.headers.get("Content-Length", "0") or 0)
            except ValueError:
                self._send_empty(400)
                return
            if length <= 0 or length > MAX_BODY_BYTES:
                self._send_empty(400)
                return

            raw_body = self.rfile.read(length)
            try:
                legacy = json.loads(raw_body.decode("utf-8"))
            except (UnicodeDecodeError, json.JSONDecodeError):
                self._send_empty(400)
                return
            if not isinstance(legacy, dict):
                self._send_empty(400)
                return

            try:
                receipt = normalize_receipt(legacy)
            except ValueError:
                registry.increment("provider_callback_rejected", labels={"reason": "invalid"})
                self._send_empty(400)
                return

            body = canonical_bytes(receipt)
            signature = sign(cfg.hmac_secret, body)
            headers = {
                "Authorization": f"Bearer {cfg.token}",
                "Content-Type": "application/json",
                "Idempotency-Key": receipt["messageId"],
                "X-Action-Version": "1",
                "X-Provider-Signature": signature,
            }

            try:
                status, api_body, api_headers = post_json(
                    cfg.receipt_api_url, body, headers, cfg.api_timeout
                )
            except HttpError:
                registry.increment("provider_callback_unavailable")
                structured_logging.log("provider_callback.api_unavailable")
                self._send_json(503, {"status": "error", "code": "dependency.unavailable"})
                return

            registry.increment(
                "provider_callback_forwarded",
                labels={"outcome": "accepted" if 200 <= status < 300 else "rejected"},
            )
            structured_logging.log("provider_callback.forwarded", apiStatus=status)
            self._send_raw(status, api_body, api_headers.get("Content-Type"))

    return AdapterHandler


def main() -> None:
    cfg = adapter_config()
    registry = Registry()
    registry.set("process_start_time_seconds", time.time())
    probes = {"gateway": observability.DependencyProbe("gateway", observability.tcp_probe(cfg.receipt_api_url))}
    server = ThreadingHTTPServer((cfg.host, cfg.port), _build_adapter(cfg, registry, probes))
    server.daemon_threads = True
    structured_logging.log(
        "adapter.started",
        port=cfg.port,
        receiptApiUrl=cfg.receipt_api_url,
    )
    try:
        server.serve_forever()
    except KeyboardInterrupt:
        pass


if __name__ == "__main__":
    main()
