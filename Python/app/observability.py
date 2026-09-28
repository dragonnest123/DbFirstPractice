from __future__ import annotations

import json
import socket
import threading
import urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from typing import Callable

from app.metrics import OPENMETRICS_CONTENT_TYPE, Registry

READINESS_TIMEOUT_SECONDS = 2.0


class DependencyProbe:
    """Cached reachability probe for a single readiness dependency."""

    def __init__(
        self,
        name: str,
        check: Callable[[], None],
        ttl: float = 2.0,
        timeout: float = READINESS_TIMEOUT_SECONDS,
    ) -> None:
        self.name = name
        self._check = check
        self._ttl = ttl
        self._timeout = timeout
        self._lock = threading.Lock()
        self._checked_at = 0.0
        self._available = False

    def available(self) -> bool:
        import time

        now = time.monotonic()
        with self._lock:
            fresh = now - self._checked_at <= self._ttl
            if fresh:
                return self._available
        try:
            self._check()
            available = True
        except Exception:  # noqa: BLE001 - readiness reports any dependency failure
            available = False
        with self._lock:
            self._checked_at = time.monotonic()
            self._available = available
        return available


def tcp_probe(url: str, timeout: float = READINESS_TIMEOUT_SECONDS) -> Callable[[], None]:
    parsed = urllib.parse.urlsplit(url)
    host = parsed.hostname or "localhost"
    port = parsed.port or (443 if parsed.scheme == "https" else 80)

    def probe() -> None:
        with socket.create_connection((host, port), timeout=timeout):
            return

    return probe


def json_body(payload: dict) -> bytes:
    return json.dumps(payload, ensure_ascii=False, separators=(",", ":")).encode("utf-8")


def respond(
    path: str,
    registry: Registry,
    probes: dict[str, DependencyProbe],
    service: str,
) -> tuple[int, str, bytes] | None:
    """Resolves one observability route, or None when the path is not ours."""
    if path == "/health/live":
        return 200, "application/json", json_body({"status": "live", "service": service})
    if path == "/health/ready":
        unavailable = sorted(name for name, probe in probes.items() if not probe.available())
        if unavailable:
            return (
                503,
                "application/json",
                json_body(
                    {
                        "status": "not_ready",
                        "code": "dependency.unavailable",
                        "dependencies": unavailable,
                    }
                ),
            )
        return 200, "application/json", json_body({"status": "ready", "service": service})
    if path == "/metrics":
        return 200, OPENMETRICS_CONTENT_TYPE, registry.render().encode("utf-8")
    return None


def build_handler(registry: Registry, probes: dict[str, DependencyProbe], service: str):
    class ObservabilityHandler(BaseHTTPRequestHandler):
        protocol_version = "HTTP/1.1"

        def log_message(self, *args):  # noqa: ANN002, ANN003
            return

        def _send(self, status: int, content_type: str, body: bytes) -> None:
            self.send_response(status)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

        def do_GET(self) -> None:  # noqa: N802 - required by BaseHTTPRequestHandler
            resolved = respond(self.path.split("?", 1)[0], registry, probes, service)
            if resolved is None:
                self._send(404, "application/json", json_body({"status": "error", "code": "not.found"}))
                return
            self._send(*resolved)

    return ObservabilityHandler


def serve(
    registry: Registry,
    probes: dict[str, DependencyProbe],
    service: str,
    host: str = "0.0.0.0",
    port: int = 8080,
) -> tuple[ThreadingHTTPServer, threading.Thread]:
    server = ThreadingHTTPServer((host, port), build_handler(registry, probes, service))
    server.daemon_threads = True
    thread = threading.Thread(target=server.serve_forever, name="observability", daemon=True)
    thread.start()
    return server, thread
