from __future__ import annotations

import json
import time

import psycopg

from app.config import ReconcilerConfig, reconciler_config
from app.db import connect


def run_once(conn: psycopg.Connection, cfg: ReconcilerConfig) -> int:
    with conn.cursor() as cursor:
        cursor.execute("SELECT delivery.reconcile_inbox(%s)", (cfg.limit,))
        value = cursor.fetchone()
    return int(value[0]) if value else 0


def main() -> None:
    cfg = reconciler_config()
    print(
        json.dumps({"ts": time.time(), "event": "reconciler.started"}, separators=(",", ":")),
        flush=True,
    )
    while True:
        try:
            with connect(cfg.pg) as conn:
                applied = run_once(conn, cfg)
            if applied:
                print(
                    json.dumps(
                        {"ts": time.time(), "event": "reconciler.applied", "count": applied},
                        separators=(",", ":"),
                    ),
                    flush=True,
                )
        except psycopg.Error as error:
            print(
                json.dumps(
                    {"ts": time.time(), "event": "reconciler.connection_error", "error": str(error)},
                    separators=(",", ":"),
                ),
                flush=True,
            )
        time.sleep(cfg.poll_interval)


if __name__ == "__main__":
    main()