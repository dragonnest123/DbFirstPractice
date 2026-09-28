from __future__ import annotations

import os
from dataclasses import dataclass

from app.failpoints import FAILPOINT_VARIABLE
from app.observability import READINESS_TIMEOUT_SECONDS


class ConfigError(RuntimeError):
    pass


def _require(name: str, default: str | None = None) -> str:
    value = os.environ.get(name) if default is None else os.environ.get(name, default)
    if value is None or value == "":
        raise ConfigError(f"missing required environment variable {name}")
    return value


def _optional(name: str) -> str | None:
    value = os.environ.get(name)
    return value.strip() if value and value.strip() else None


def _int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, str(default)))
    except ValueError as error:
        raise ConfigError(f"invalid integer for {name}") from error


def _milliseconds(name: str, default: int) -> float:
    value = _int(name, default)
    if value <= 0:
        raise ConfigError(f"{name} must be positive")
    return value / 1000.0


def _test_profile() -> bool:
    return os.environ.get("COURSE_TEST_PROFILE") == "1"


def _failpoint_name() -> str | None:
    return _optional(FAILPOINT_VARIABLE)


@dataclass(frozen=True)
class PostgresConfig:
    host: str
    port: int
    database: str
    user: str
    password: str
    appname: str


def _postgres(appname: str) -> PostgresConfig:
    return PostgresConfig(
        host=_require("PGHOST", "postgres"),
        port=_int("PGPORT", 5432),
        database=_require("PGDATABASE", "course"),
        user=_require("PGUSER"),
        password=_require("PGPASSWORD"),
        appname=_require("PGAPPNAME", appname),
    )


@dataclass(frozen=True)
class ObservabilityConfig:
    host: str
    port: int
    service: str
    failpoint: str | None
    readiness_timeout: float = READINESS_TIMEOUT_SECONDS


def _observability(
    service: str,
    host_variable: str,
    port_variable: str,
    default_port: int = 8080,
) -> ObservabilityConfig:
    return ObservabilityConfig(
        host=_require(host_variable, "0.0.0.0"),
        port=_int(port_variable, default_port),
        service=service,
        failpoint=_failpoint_name(),
    )


@dataclass(frozen=True)
class DispatcherConfig:
    pg: PostgresConfig
    observability: ObservabilityConfig
    provider_url: str
    owner: str
    poll_interval: float
    provider_timeout: float
    claim_batch: int


def dispatcher_config() -> DispatcherConfig:
    test = _test_profile()
    owner = _require("OUTBOX_OWNER", "outbox-dispatcher")
    return DispatcherConfig(
        pg=_postgres(owner),
        observability=_observability(owner, "OBSERVABILITY_HOST", "OBSERVABILITY_PORT"),
        provider_url=_require("PROVIDER_URL"),
        owner=owner,
        poll_interval=0.1 if test else _milliseconds("COURSE_OUTBOX_POLL_MS", 2000),
        provider_timeout=0.5 if test else _milliseconds("COURSE_PROVIDER_TIMEOUT_MS", 5000),
        claim_batch=_int("OUTBOX_CLAIM_BATCH", 5),
    )


@dataclass(frozen=True)
class AdapterConfig:
    capability: str
    token: str
    hmac_secret: str
    receipt_api_url: str
    observability: ObservabilityConfig
    host: str
    port: int
    api_timeout: float


def adapter_config() -> AdapterConfig:
    test = _test_profile()
    capability = _require("PROVIDER_CALLBACK_CAPABILITY")
    return AdapterConfig(
        capability=capability,
        token=_require("PROVIDER_CALLBACK_TOKEN"),
        hmac_secret=_require("PROVIDER_HMAC_SECRET"),
        receipt_api_url=_require("RECEIPT_API_URL"),
        observability=_observability(
            capability, "OBSERVABILITY_HOST", "OBSERVABILITY_PORT", default_port=8082
        ),
        host=_require("ADAPTER_LISTEN_HOST", os.environ.get("ADAPTER_HOST", "0.0.0.0")),
        port=_int("ADAPTER_LISTEN_PORT", _int("ADAPTER_PORT", 8082)),
        api_timeout=0.5 if test else _milliseconds("ADAPTER_API_TIMEOUT_MS", 5000),
    )


@dataclass(frozen=True)
class ReconcilerConfig:
    pg: PostgresConfig
    observability: ObservabilityConfig
    poll_interval: float
    limit: int


def reconciler_config() -> ReconcilerConfig:
    test = _test_profile()
    appname = _require("PGAPPNAME", "inbox-reconciler")
    return ReconcilerConfig(
        pg=_postgres(appname),
        observability=_observability(appname, "OBSERVABILITY_HOST", "OBSERVABILITY_PORT"),
        poll_interval=0.5 if test else _milliseconds("COURSE_INBOX_POLL_MS", 2000),
        limit=_int("INBOX_RECONCILE_LIMIT", 50),
    )
