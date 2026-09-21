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
        "PROVIDER_URL",
        "OUTBOX_OWNER",
        "COURSE_TEST_PROFILE",
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
    assert cfg.port == 8090


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