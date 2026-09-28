from __future__ import annotations

import pytest

from app import config


@pytest.fixture(autouse=True)
def _clean_environment(monkeypatch):
    for name in (
        "PGUSER",
        "PGPASSWORD",
        "PGHOST",
        "PGPORT",
        "PGDATABASE",
        "PGAPPNAME",
        "PROVIDER_URL",
        "OUTBOX_OWNER",
        "COURSE_TEST_PROFILE",
        "COURSE_FAILPOINT",
        "COURSE_OUTBOX_POLL_MS",
        "COURSE_INBOX_POLL_MS",
        "COURSE_PROVIDER_TIMEOUT_MS",
        "OBSERVABILITY_HOST",
        "OBSERVABILITY_PORT",
        "ADAPTER_HOST",
        "ADAPTER_PORT",
        "ADAPTER_LISTEN_HOST",
        "ADAPTER_LISTEN_PORT",
        "PROVIDER_CALLBACK_CAPABILITY",
        "PROVIDER_CALLBACK_TOKEN",
        "PROVIDER_HMAC_SECRET",
        "RECEIPT_API_URL",
    ):
        monkeypatch.delenv(name, raising=False)


def test_dispatcher_requires_provider_url(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("PROVIDER_URL", "")
    with pytest.raises(config.ConfigError):
        config.dispatcher_config()


def test_dispatcher_requires_database_password(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PROVIDER_URL", "http://provider:8081")
    with pytest.raises(config.ConfigError):
        config.dispatcher_config()


def test_adapter_rejects_missing_secrets(monkeypatch) -> None:
    with pytest.raises(config.ConfigError):
        config.adapter_config()


def test_adapter_accepts_full_configuration(monkeypatch) -> None:
    monkeypatch.setenv("PROVIDER_CALLBACK_CAPABILITY", "cap")
    monkeypatch.setenv("PROVIDER_CALLBACK_TOKEN", "token")
    monkeypatch.setenv("PROVIDER_HMAC_SECRET", "secret")
    monkeypatch.setenv("RECEIPT_API_URL", "http://gateway:8080/api/receipt/accept")
    cfg = config.adapter_config()
    assert cfg.capability == "cap"
    assert cfg.port == 8082
    assert cfg.observability.port == 8082


def test_adapter_listen_port_overrides_default(monkeypatch) -> None:
    monkeypatch.setenv("PROVIDER_CALLBACK_CAPABILITY", "cap")
    monkeypatch.setenv("PROVIDER_CALLBACK_TOKEN", "token")
    monkeypatch.setenv("PROVIDER_HMAC_SECRET", "secret")
    monkeypatch.setenv("RECEIPT_API_URL", "http://gateway:8080/api/receipt/accept")
    monkeypatch.setenv("ADAPTER_LISTEN_PORT", "9091")
    assert config.adapter_config().port == 9091


def test_reconciler_requires_database_password(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "inbox_reconciler")
    with pytest.raises(config.ConfigError):
        config.reconciler_config()


def test_test_profile_shortens_poll_interval(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("PROVIDER_URL", "http://provider:8081")
    monkeypatch.setenv("COURSE_TEST_PROFILE", "1")
    assert config.dispatcher_config().poll_interval <= 0.5


def test_production_poll_interval_follows_configuration(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("PROVIDER_URL", "http://provider:8081")
    monkeypatch.setenv("COURSE_OUTBOX_POLL_MS", "1500")
    assert config.dispatcher_config().poll_interval == 1.5


def test_poll_interval_must_be_positive(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "inbox_reconciler")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("COURSE_INBOX_POLL_MS", "0")
    with pytest.raises(config.ConfigError):
        config.reconciler_config()


def test_failpoint_is_read_from_environment(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("PROVIDER_URL", "http://provider:8081")
    monkeypatch.setenv("COURSE_FAILPOINT", "after_outbox_claim")
    assert config.dispatcher_config().observability.failpoint == "after_outbox_claim"


def test_blank_failpoint_is_absent(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "inbox_reconciler")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("COURSE_FAILPOINT", "  ")
    assert config.reconciler_config().observability.failpoint is None


def test_application_name_comes_from_pgappname(monkeypatch) -> None:
    monkeypatch.setenv("PGUSER", "outbox_dispatcher")
    monkeypatch.setenv("PGPASSWORD", "x")
    monkeypatch.setenv("PROVIDER_URL", "http://provider:8081")
    monkeypatch.setenv("PGAPPNAME", "week4-public-outbox-dispatcher-b")
    assert config.dispatcher_config().pg.appname == "week4-public-outbox-dispatcher-b"