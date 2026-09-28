#!/usr/bin/env bash
set -euo pipefail

export COURSE_MIGRATOR_PASSWORD="${COURSE_MIGRATOR_PASSWORD:-migration}"
export COURSE_PUBLISHER_PASSWORD="${COURSE_PUBLISHER_PASSWORD:-publication}"
export COURSE_RUNTIME_PASSWORD="${COURSE_RUNTIME_PASSWORD:-runtime}"
export COURSE_WORKER_PASSWORD="${COURSE_WORKER_PASSWORD:-worker}"
export COURSE_OUTBOX_PASSWORD="${COURSE_OUTBOX_PASSWORD:-outbox}"
export COURSE_INBOX_PASSWORD="${COURSE_INBOX_PASSWORD:-inbox}"

psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER:-postgres}" --dbname "${POSTGRES_DB:-course}" <<'EOSQL'
\getenv migrator_pw COURSE_MIGRATOR_PASSWORD
\getenv publisher_pw COURSE_PUBLISHER_PASSWORD
\getenv runtime_pw COURSE_RUNTIME_PASSWORD
\getenv worker_pw COURSE_WORKER_PASSWORD
\getenv outbox_pw COURSE_OUTBOX_PASSWORD
\getenv inbox_pw COURSE_INBOX_PASSWORD
ALTER ROLE course_migration PASSWORD :'migrator_pw';
ALTER ROLE course_publication PASSWORD :'publisher_pw';
ALTER ROLE course_runtime PASSWORD :'runtime_pw';
ALTER ROLE workflow_worker PASSWORD :'worker_pw';
ALTER ROLE outbox_dispatcher PASSWORD :'outbox_pw';
ALTER ROLE inbox_reconciler PASSWORD :'inbox_pw';
EOSQL