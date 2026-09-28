from __future__ import annotations

import threading
from dataclasses import dataclass, field
from typing import Iterable, Mapping

COUNTER = "counter"
GAUGE = "gauge"

OPENMETRICS_CONTENT_TYPE = "application/openmetrics-text; version=1.0.0; charset=utf-8"

_LABEL_ESCAPES = ((("\\", "\\\\")), ('"', '\\"'), ("\n", "\\n"))


def _escape_label(value: str) -> str:
    for raw, encoded in _LABEL_ESCAPES:
        value = value.replace(raw, encoded)
    return value


def _render_value(value: float) -> str:
    if value != value:  # NaN
        return "NaN"
    if value in (float("inf"), float("-inf")):
        return "+Inf" if value > 0 else "-Inf"
    if value == int(value) and abs(value) < 1e15:
        return str(int(value))
    return f"{value:.6f}".rstrip("0").rstrip(".") or "0"


@dataclass(frozen=True)
class Family:
    name: str
    kind: str
    help: str
    samples: dict[tuple[tuple[str, str], ...], float] = field(default_factory=dict)


class Registry:
    """Process-local OpenMetrics registry.

    Only bounded, low-cardinality labels are allowed: identifier-shaped label
    names are rejected so a scrape can never carry unbounded series.
    """

    FORBIDDEN_LABELS = frozenset(
        {
            "correlation_id",
            "request_id",
            "operation_id",
            "process_id",
            "step_instance_id",
            "job_id",
            "execution_id",
            "attempt_id",
            "outbox_id",
            "external_request_id",
            "message_id",
            "decision_id",
            "correlationId",
            "requestId",
            "operationId",
            "processId",
            "stepInstanceId",
            "jobId",
            "executionId",
            "attemptId",
            "outboxId",
            "externalRequestId",
            "messageId",
            "decisionId",
        }
    )

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._families: dict[str, Family] = {}

    def register(self, name: str, kind: str, help_text: str) -> None:
        with self._lock:
            self._families.setdefault(name, Family(name, kind, help_text))

    @staticmethod
    def _key(labels: Mapping[str, str] | None) -> tuple[tuple[str, str], ...]:
        if not labels:
            return ()
        for key in labels:
            if key in Registry.FORBIDDEN_LABELS:
                raise ValueError(f"label {key} is not allowed in metrics")
        return tuple(sorted((str(key), str(value)) for key, value in labels.items()))

    def increment(self, name: str, amount: float = 1.0, labels: Mapping[str, str] | None = None) -> None:
        key = self._key(labels)
        with self._lock:
            family = self._families.setdefault(name, Family(name, COUNTER, name))
            family.samples[key] = family.samples.get(key, 0.0) + amount

    def set(self, name: str, value: float, labels: Mapping[str, str] | None = None) -> None:
        key = self._key(labels)
        with self._lock:
            family = self._families.setdefault(name, Family(name, GAUGE, name))
            family.samples[key] = float(value)

    def value(self, name: str, labels: Mapping[str, str] | None = None) -> float:
        key = self._key(labels)
        with self._lock:
            return self._families.get(name, Family(name, GAUGE, name)).samples.get(key, 0.0)

    def render(self) -> str:
        with self._lock:
            families: Iterable[Family] = [self._families[name] for name in sorted(self._families)]
            snapshot = [(family, dict(family.samples)) for family in families]
        lines: list[str] = []
        for family, samples in snapshot:
            lines.append(f"# HELP {family.name} {family.help}")
            lines.append(f"# TYPE {family.name} {family.kind}")
            sample_name = f"{family.name}_total" if family.kind == COUNTER else family.name
            for key in sorted(samples):
                labels = (
                    "{" + ",".join(f'{key_name}="{_escape_label(value)}"' for key_name, value in key) + "}"
                    if key
                    else ""
                )
                lines.append(f"{sample_name}{labels} {_render_value(samples[key])}")
        lines.append("# EOF")
        return "\n".join(lines) + "\n"
