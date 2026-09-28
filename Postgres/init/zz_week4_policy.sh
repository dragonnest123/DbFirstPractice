#!/usr/bin/env bash
set -euo pipefail

export COURSE_AUTOCHECK_PASSWORD="${COURSE_AUTOCHECK_PASSWORD:-autocheck}"
export COURSE_OUTBOX_MAX_ATTEMPTS="${COURSE_OUTBOX_MAX_ATTEMPTS:-4}"
export COURSE_OUTBOX_BACKOFF_BASE_MS="${COURSE_OUTBOX_BACKOFF_BASE_MS:-200}"
export COURSE_OUTBOX_BACKOFF_MAX_MS="${COURSE_OUTBOX_BACKOFF_MAX_MS:-800}"
export COURSE_OUTBOX_JITTER_MAX_MS="${COURSE_OUTBOX_JITTER_MAX_MS:-100}"
export COURSE_OUTBOX_LEASE_MS="${COURSE_OUTBOX_LEASE_MS:-2000}"
export COURSE_JOB_LEASE_MS="${COURSE_JOB_LEASE_MS:-2000}"

psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname "${POSTGRES_DB:-course}" <<'EOSQL'
\getenv autocheck_pw COURSE_AUTOCHECK_PASSWORD
\getenv outbox_max_attempts COURSE_OUTBOX_MAX_ATTEMPTS
\getenv outbox_backoff_base_ms COURSE_OUTBOX_BACKOFF_BASE_MS
\getenv outbox_backoff_max_ms COURSE_OUTBOX_BACKOFF_MAX_MS
\getenv outbox_jitter_max_ms COURSE_OUTBOX_JITTER_MAX_MS
\getenv outbox_lease_ms COURSE_OUTBOX_LEASE_MS
\getenv job_lease_ms COURSE_JOB_LEASE_MS

ALTER ROLE autocheck_reader PASSWORD :'autocheck_pw';

SELECT delivery.apply_outbox_policy(
    :outbox_max_attempts::integer,
    :outbox_backoff_base_ms::integer,
    :outbox_backoff_max_ms::integer,
    :outbox_jitter_max_ms::integer,
    :outbox_lease_ms::integer);

-- Effective runtime configuration, readable by the troubleshooting runbook.
CREATE TABLE IF NOT EXISTS public.course_runtime_config (
    name text PRIMARY KEY,
    value text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
ALTER TABLE public.course_runtime_config OWNER TO course_owner;

INSERT INTO public.course_runtime_config(name, value) VALUES
    ('outbox.max_attempts', :'outbox_max_attempts'),
    ('outbox.backoff_base_ms', :'outbox_backoff_base_ms'),
    ('outbox.backoff_max_ms', :'outbox_backoff_max_ms'),
    ('outbox.jitter_max_ms', :'outbox_jitter_max_ms'),
    ('outbox.lease_ms', :'outbox_lease_ms'),
    ('job.lease_ms', :'job_lease_ms')
ON CONFLICT (name) DO UPDATE
SET value = EXCLUDED.value, updated_at = clock_timestamp();

REVOKE ALL ON public.course_runtime_config FROM PUBLIC;
GRANT SELECT ON public.course_runtime_config TO course_runtime, workflow_worker, course_target;
EOSQL
