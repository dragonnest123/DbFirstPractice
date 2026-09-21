CREATE SCHEMA IF NOT EXISTS delivery;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'outbox_dispatcher') THEN
        CREATE ROLE outbox_dispatcher LOGIN PASSWORD 'outbox';
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'inbox_reconciler') THEN
        CREATE ROLE inbox_reconciler LOGIN PASSWORD 'inbox';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS delivery.external_request (
    external_request_id text PRIMARY KEY,
    operation_id uuid NOT NULL UNIQUE,
    correlation_id uuid NOT NULL,
    amount numeric(20,2) NOT NULL,
    currency text NOT NULL CHECK (currency = 'RUB'),
    state text NOT NULL CHECK (state IN ('CREATED','SENT','CONFIRMED')),
    payload_hash text NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{64}$'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS delivery.outbox (
    outbox_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    external_request_id text NOT NULL UNIQUE REFERENCES delivery.external_request(external_request_id),
    correlation_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('PENDING','LEASED','RETRY_WAIT','DELIVERED','DEAD','CONFIRMED')),
    lease_owner text,
    lease_version bigint NOT NULL DEFAULT 0,
    lease_until timestamptz,
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz,
    last_error_code text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    delivered_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_outbox_claimable ON delivery.outbox(state, next_attempt_at);

CREATE TABLE IF NOT EXISTS delivery.receipt (
    message_id text PRIMARY KEY,
    external_request_id text NOT NULL REFERENCES delivery.external_request(external_request_id),
    message_version integer NOT NULL CHECK (message_version >= 1),
    outcome text NOT NULL CHECK (outcome IN ('COMPLETED','REJECTED')),
    signature_valid boolean NOT NULL,
    body_hash text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    body jsonb NOT NULL,
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_receipt_external ON delivery.receipt(external_request_id);

CREATE TABLE IF NOT EXISTS delivery.inbox (
    message_id text PRIMARY KEY,
    external_request_id text NOT NULL REFERENCES delivery.external_request(external_request_id),
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    signal_type text NOT NULL,
    body_hash text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    payload jsonb NOT NULL,
    state text NOT NULL CHECK (state IN ('RECEIVED','APPLIED','CONFLICT')),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    applied_at timestamptz
);
CREATE INDEX IF NOT EXISTS ix_inbox_reconcilable ON delivery.inbox(state, signal_type, received_at);

CREATE TABLE IF NOT EXISTS payment.decisions (
    decision_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    step_instance_id uuid NOT NULL REFERENCES workflow.step_instance(step_instance_id),
    source text NOT NULL CHECK (source IN ('LIMIT_RULE','MANUAL')),
    principal text,
    reason_hash text CHECK (reason_hash ~ '^[0-9a-f]{64}$'),
    outcome text NOT NULL CHECK (outcome IN ('APPROVED','REJECTED')),
    rule_version text,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (step_instance_id)
);
CREATE INDEX IF NOT EXISTS ix_decisions_process ON payment.decisions(process_id);

CREATE OR REPLACE FUNCTION delivery.block_delete()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'delivery.immutable: delivery records cannot be deleted';
END;
$$;

DROP TRIGGER IF EXISTS trg_external_request_no_delete ON delivery.external_request;
CREATE TRIGGER trg_external_request_no_delete
    BEFORE DELETE ON delivery.external_request
    FOR EACH ROW EXECUTE FUNCTION delivery.block_delete();

DROP TRIGGER IF EXISTS trg_outbox_no_delete ON delivery.outbox;
CREATE TRIGGER trg_outbox_no_delete
    BEFORE DELETE ON delivery.outbox
    FOR EACH ROW EXECUTE FUNCTION delivery.block_delete();

DROP TRIGGER IF EXISTS trg_receipt_no_delete ON delivery.receipt;
CREATE TRIGGER trg_receipt_no_delete
    BEFORE DELETE ON delivery.receipt
    FOR EACH ROW EXECUTE FUNCTION delivery.block_delete();

DROP TRIGGER IF EXISTS trg_inbox_no_delete ON delivery.inbox;
CREATE TRIGGER trg_inbox_no_delete
    BEFORE DELETE ON delivery.inbox
    FOR EACH ROW EXECUTE FUNCTION delivery.block_delete();

DROP TRIGGER IF EXISTS trg_decision_no_delete ON payment.decisions;
CREATE TRIGGER trg_decision_no_delete
    BEFORE DELETE ON payment.decisions
    FOR EACH ROW EXECUTE FUNCTION delivery.block_delete();

ALTER TABLE delivery.external_request OWNER TO course_owner;
ALTER TABLE delivery.outbox OWNER TO course_owner;
ALTER TABLE delivery.receipt OWNER TO course_owner;
ALTER TABLE delivery.inbox OWNER TO course_owner;
ALTER TABLE payment.decisions OWNER TO course_owner;
ALTER FUNCTION delivery.block_delete() OWNER TO course_owner;

GRANT USAGE ON SCHEMA delivery TO course_owner, course_target, course_runtime,
    outbox_dispatcher, inbox_reconciler;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA delivery TO course_target;
GRANT SELECT, INSERT, UPDATE, DELETE ON payment.decisions TO course_target;
GRANT SELECT ON delivery.external_request, delivery.outbox, delivery.receipt,
    delivery.inbox, payment.decisions TO course_runtime;
REVOKE ALL ON FUNCTION delivery.block_delete() FROM PUBLIC;
GRANT EXECUTE ON FUNCTION delivery.block_delete() TO course_owner;