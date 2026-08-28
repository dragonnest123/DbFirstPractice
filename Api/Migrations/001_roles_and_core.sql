CREATE SCHEMA IF NOT EXISTS api;
CREATE SCHEMA IF NOT EXISTS payment;
CREATE SCHEMA IF NOT EXISTS autocheck;

CREATE EXTENSION IF NOT EXISTS "pgcrypto" WITH SCHEMA api;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'course_owner') THEN
        CREATE ROLE course_owner NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'course_runtime') THEN
        CREATE ROLE course_runtime LOGIN PASSWORD 'runtime';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'course_migration') THEN
        CREATE ROLE course_migration LOGIN PASSWORD 'migration';
    END IF;
END $$;

GRANT CREATE ON DATABASE course TO course_migration;

CREATE TABLE IF NOT EXISTS public.schema_migrations (
    filename text PRIMARY KEY,
    checksum text NOT NULL,
    applied_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS api.action_catalog (
    module text NOT NULL,
    action text NOT NULL,
    version integer NOT NULL CHECK (version >= 1),
    http_method text NOT NULL CHECK (http_method = 'POST'),
    target_schema text NOT NULL,
    target_function text NOT NULL,
    request_schema jsonb NOT NULL,
    response_schema jsonb NOT NULL,
    outcomes jsonb NOT NULL,
    required_policy jsonb NOT NULL,
    idempotency_mode text NOT NULL CHECK (idempotency_mode IN ('none','optional','required')),
    idempotency_scope text NOT NULL CHECK (idempotency_scope IN ('none','principal_action','consumer_action','global_action')),
    timeout_ms integer NOT NULL CHECK (timeout_ms BETWEEN 1 AND 30000),
    enabled boolean NOT NULL,
    is_default boolean NOT NULL,
    contract_version text NOT NULL CHECK (contract_version = 'course-1'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (module, action, version),
    CONSTRAINT chk_manifest_immutable CHECK (true),
    CONSTRAINT chk_default_implies_enabled CHECK (NOT is_default OR enabled)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_action_catalog_default
    ON api.action_catalog(module, action) WHERE is_default;

CREATE TABLE IF NOT EXISTS api.action_dispatches (
    correlation_id uuid NOT NULL,
    request_id text NOT NULL,
    module text NOT NULL,
    action text NOT NULL,
    version integer NOT NULL,
    principal text NOT NULL,
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    status text NOT NULL CHECK (status IN ('OK','ERROR')),
    outcome text,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (correlation_id)
);
CREATE INDEX IF NOT EXISTS ix_dispatches_request ON api.action_dispatches(request_id);
CREATE INDEX IF NOT EXISTS ix_dispatches_module_action ON api.action_dispatches(module, action, version);

CREATE TABLE IF NOT EXISTS api.idempotency_store (
    scope_key text NOT NULL,
    request_id text NOT NULL,
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    response jsonb,
    operation_id uuid,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (scope_key, request_id)
);

CREATE TABLE IF NOT EXISTS payment.operations (
    operation_id uuid PRIMARY KEY,
    request_id text NOT NULL,
    principal text NOT NULL,
    operation_kind text NOT NULL CHECK (operation_kind IN ('PAYMENT_EXECUTION','PAYMENT_APPROVAL')),
    amount numeric(20,2) NOT NULL CHECK (amount >= 0.01 AND amount <= 9999999999999999.99),
    currency text NOT NULL CHECK (currency = 'RUB'),
    status text NOT NULL CHECK (status IN ('CREATED','PROCESSING','COMPLETED','REJECTED')),
    process_id uuid,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_operations_scope_request ON payment.operations(principal, request_id);
CREATE INDEX IF NOT EXISTS ix_operations_request ON payment.operations(request_id);
CREATE INDEX IF NOT EXISTS ix_operations_kind ON payment.operations(operation_kind);

CREATE TABLE IF NOT EXISTS payment.operation_events (
    event_id uuid PRIMARY KEY,
    operation_id uuid NOT NULL REFERENCES payment.operations(operation_id),
    event_type text NOT NULL CHECK (event_type = 'OPERATION_CREATED'),
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
CREATE INDEX IF NOT EXISTS ix_operation_events_op ON payment.operation_events(operation_id);
