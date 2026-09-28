from __future__ import annotations

import threading
import urllib.error
import urllib.request
from http.server import ThreadingHTTPServer

import pytest

from app import observability
from app.metrics import Registry


def test_render_is_parseable_openmetrics() -> None:
    registry = Registry()
    registry.increment("outbox_dispatch_attempts", labels={"outcome": "delivered"})
    registry.increment("outbox_dispatch_attempts", labels={"outcome": "delivered"})
    registry.set("outbox_pending", 3)
    text = registry.render()
    assert text.endswith("# EOF\n")
    assert "\r" not in text
    assert "# TYPE outbox_dispatch_attempts counter" in text
    assert "outbox_dispatch_attempts_total{outcome=\"delivered\"} 2" in text
    assert "# TYPE outbox_pending gauge" in text
    assert "outbox_pending 3" in text


def test_identifier_labels_are_rejected() -> None:
    registry = Registry()
    with pytest.raises(ValueError):
        registry.increment("x", labels={"correlation_id": "abc"})
    with pytest.raises(ValueError):
        registry.set("y", 1, labels={"operationId": "abc"})


def test_bounded_label_values_are_escaped() -> None:
    registry = Registry()
    registry.increment("attempts", labels={"outcome": 'a"b\\c'})
    assert 'attempts_total{outcome="a\\"b\\\\c"} 1' in registry.render()


def _serve(registry: Registry, probes: dict) -> str:
    server = ThreadingHTTPServer(("127.0.0.1", 0), observability.build_handler(registry, probes, "svc"))
    server.daemon_threads = True
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return f"http://127.0.0.1:{server.server_address[1]}"


def _get(url: str) -> tuple[int, str, bytes]:
    try:
        with urllib.request.urlopen(url, timeout=5) as response:
            return response.status, response.headers.get("Content-Type", ""), response.read()
    except urllib.error.HTTPError as error:
        return error.code, error.headers.get("Content-Type", ""), error.read()


def test_live_is_independent_of_dependencies() -> None:
    failing = observability.DependencyProbe("postgres", lambda: (_ for _ in ()).throw(OSError("down")))
    base = _serve(Registry(), {"postgres": failing})
    status, content_type, body = _get(f"{base}/health/live")
    assert status == 200
    assert content_type.startswith("application/json")
    assert b'"live"' in body


def test_ready_reports_dependency_unavailable() -> None:
    failing = observability.DependencyProbe("postgres", lambda: (_ for _ in ()).throw(OSError("down")))
    base = _serve(Registry(), {"postgres": failing})
    status, _, body = _get(f"{base}/health/ready")
    assert status == 503
    assert b"dependency.unavailable" in body


def test_ready_is_ok_when_dependency_answers() -> None:
    base = _serve(Registry(), {"postgres": observability.DependencyProbe("postgres", lambda: None)})
    status, _, body = _get(f"{base}/health/ready")
    assert status == 200
    assert b'"ready"' in body


def test_metrics_use_openmetrics_media_type() -> None:
    base = _serve(Registry(), {})
    status, content_type, body = _get(f"{base}/metrics")
    assert status == 200
    assert content_type == "application/openmetrics-text; version=1.0.0; charset=utf-8"
    assert body.endswith(b"# EOF\n")


def test_unknown_route_is_not_found() -> None:
    base = _serve(Registry(), {})
    status, _, _ = _get(f"{base}/nope")
    assert status == 404
