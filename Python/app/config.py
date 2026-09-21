from __future__ import annotations

import os
from dataclasses import dataclass


class ConfigError(RuntimeError):
    pass


def _require(name: str, default: str | None = None) -> str:
    value = os.environ.get(name) if default is None else os.environ.get(name, default)
    if value is None or value == "":
        raise ConfigError(f"missing required environment variable {name}")
    return value


def _int(name: str, default: int) -> int:
    try:
        return int(os.environ.get(name, str(default)))
    except ValueError as error:
        raise ConfigError(f"invalid integer for {name}") from error


def _test_profile() -> bool:
    return os.environ.get("COURSE_TEST_PROFILE") == "1"


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
        appname=appname,
    )


@dataclass(frozen=True)
class DispatcherConfig:
    pg: PostgresConfig
    provider_url: str
    owner: str
    poll_interval: float
    provider_timeout: float
    claim_batch: int


def dispatcher_config() -> DispatcherConfig:
    test = _test_profile()
    return DispatcherConfig(
        pg=_postgres("outbox-dispatcher"),
        provider_url=_require("PROVIDER_URL"),
        owner=_require("OUTBOX_OWNER", "outbox-dispatcher"),
        poll_interval=0.2 if test else 2.0,
        provider_timeout=0.5 if test else 5.0,
        claim_batch=_int("OUTBOX_CLAIM_BATCH", 5),
    )


@dataclass(frozen=True)
class AdapterConfig:
    capability: str
    token: str
    hmac_secret: str
    receipt_api_url: str
    host: str
    port: int
    api_timeout: float


def adapter_config() -> AdapterConfig:
    test = _test_profile()
    return AdapterConfig(
        capability=_require("PROVIDER_CALLBACK_CAPABILITY"),
        token=_require("PROVIDER_CALLBACK_TOKEN"),
        hmac_secret=_require("PROVIDER_HMAC_SECRET"),
        receipt_api_url=_require("RECEIPT_API_URL"),
        host=_require("ADAPTER_HOST", "0.0.0.0"),
        port=_int("ADAPTER_PORT", 8090),
        api_timeout=0.5 if test else 5.0,
    )


@dataclass(frozen=True)
class ReconcilerConfig:
    pg: PostgresConfig
    poll_interval: float
    limit: int


def reconciler_config() -> ReconcilerConfig:
    test = _test_profile()
    return ReconcilerConfig(
        pg=_postgres("inbox-reconciler"),
        poll_interval=0.2 if test else 2.0,
        limit=_int("INBOX_RECONCILE_LIMIT", 50),
    )