from __future__ import annotations

import psycopg

from app.config import PostgresConfig


def connect(cfg: PostgresConfig) -> psycopg.Connection:
    return psycopg.connect(
        host=cfg.host,
        port=cfg.port,
        dbname=cfg.database,
        user=cfg.user,
        password=cfg.password,
        application_name=cfg.appname,
        autocommit=True,
    )