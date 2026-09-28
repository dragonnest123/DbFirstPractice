from __future__ import annotations

import time

import psycopg

from app import observability, structured_logging
from app.config import ReconcilerConfig, reconciler_config
from app.db import connect
from app.failpoints import FailpointController
from app.metrics import Registry


def run_once(
    conn: psycopg.Connection,
    cfg: ReconcilerConfig,
    registry: Registry | None = None,
) -> int:
    registry = registry or Registry()
    with conn.cursor() as cursor:
        cursor.execute("SELECT delivery.reconcile_inbox(%s)", (cfg.limit,))
        value = cursor.fetchone()
    applied = int(value[0]) if value else 0
    if applied:
        registry.increment("inbox_messages_applied", applied)
    return applied


def _database_probe(cfg: ReconcilerConfig):
    def probe() -> None:
        with connect(cfg.pg) as conn:
            with conn.cursor() as cursor:
                cursor.execute("SELECT 1")
                cursor.fetchone()

    return probe


def main() -> None:
    cfg = reconciler_config()
    failpoints = FailpointController.from_environment()
    registry = Registry()
    registry.set("process_start_time_seconds", time.time())
    observability.serve(
        registry,
        {"postgres": observability.DependencyProbe("postgres", _database_probe(cfg))},
        cfg.observability.service,
        cfg.observability.host,
        cfg.observability.port,
    )
    structured_logging.log(
        "reconciler.started",
        applicationName=cfg.pg.appname,
        failpoint=failpoints.describe(),
    )
    if failpoints.armed_name:
        structured_logging.log(
            "reconciler.failpoint_not_supported",
            failpoint=failpoints.armed_name,
        )
    while True:
        try:
            with connect(cfg.pg) as conn:
                applied = run_once(conn, cfg, registry)
            if applied:
                structured_logging.log("inbox.applied", count=applied)
        except psycopg.Error as error:
            registry.increment("reconciler_database_errors")
            structured_logging.log("reconciler.connection_error", error=type(error).__name__)
        time.sleep(cfg.poll_interval)


if __name__ == "__main__":
    main()
