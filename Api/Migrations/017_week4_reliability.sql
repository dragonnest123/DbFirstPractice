-- Week 4: delivery reliability, read-only projection role and diagnostics actions.

CREATE SCHEMA IF NOT EXISTS diagnostics;

-- ---------------------------------------------------------------------------
-- Read-only projection role used by the trusted checker channel.
-- ---------------------------------------------------------------------------

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'autocheck_reader') THEN
        CREATE ROLE autocheck_reader LOGIN PASSWORD 'autocheck';
    END IF;
END $$;

ALTER ROLE autocheck_reader NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT
    NOREPLICATION NOBYPASSRLS;

-- PUBLIC holds CONNECT/TEMP on a new database; the reader must not inherit either.
REVOKE TEMPORARY, CREATE ON DATABASE course FROM PUBLIC;
REVOKE ALL ON DATABASE course FROM autocheck_reader;
GRANT CONNECT ON DATABASE course TO autocheck_reader;

ALTER ROLE autocheck_reader SET default_transaction_read_only = on;
ALTER ROLE autocheck_reader SET search_path = autocheck, pg_catalog;

GRANT USAGE ON SCHEMA autocheck TO autocheck_reader;

-- ---------------------------------------------------------------------------
-- Outbox policy: exponential backoff with jitter, short delivery lease.
-- ---------------------------------------------------------------------------

ALTER TABLE delivery.outbox_policy
    ADD COLUMN IF NOT EXISTS base_ms integer NOT NULL DEFAULT 200,
    ADD COLUMN IF NOT EXISTS max_ms integer NOT NULL DEFAULT 800,
    ADD COLUMN IF NOT EXISTS jitter_max_ms integer NOT NULL DEFAULT 100,
    ADD COLUMN IF NOT EXISTS lease_ms integer NOT NULL DEFAULT 2000;

ALTER TABLE delivery.outbox_policy
    ADD CONSTRAINT chk_outbox_policy_max_attempts CHECK (max_attempts BETWEEN 1 AND 10),
    ADD CONSTRAINT chk_outbox_policy_backoff CHECK (base_ms > 0 AND max_ms >= base_ms),
    ADD CONSTRAINT chk_outbox_policy_jitter CHECK (jitter_max_ms >= 0),
    ADD CONSTRAINT chk_outbox_policy_lease CHECK (lease_ms >= 100);

UPDATE delivery.outbox_policy
SET max_attempts = 4,
    delays_ms = ARRAY[200, 400, 800],
    base_ms = 200,
    max_ms = 800,
    jitter_max_ms = 100,
    lease_ms = 2000
WHERE id;

-- Terminal delivery state is recorded with its own timestamp.
ALTER TABLE delivery.outbox
    ADD COLUMN IF NOT EXISTS dead_at timestamptz;

CREATE INDEX IF NOT EXISTS ix_outbox_dead ON delivery.outbox(state, dead_at)
    WHERE state = 'DEAD';

-- ---------------------------------------------------------------------------
-- Runtime-configurable policy, owned by the migration role only.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION delivery.apply_outbox_policy(
    p_max_attempts integer,
    p_base_ms integer,
    p_max_ms integer,
    p_jitter_max_ms integer,
    p_lease_ms integer
)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, delivery
AS $$
DECLARE
    v_delays integer[];
BEGIN
    IF p_max_attempts IS NULL OR p_max_attempts < 1 OR p_max_attempts > 10 THEN
        RETURN jsonb_build_object(
            'status', 'error', 'code', 'policy.invalid',
            'message', 'max attempts must be between 1 and 10');
    END IF;
    IF p_base_ms IS NULL OR p_base_ms < 1
       OR p_max_ms IS NULL OR p_max_ms < p_base_ms
       OR p_jitter_max_ms IS NULL OR p_jitter_max_ms < 0
       OR p_lease_ms IS NULL OR p_lease_ms < 100 THEN
        RETURN jsonb_build_object(
            'status', 'error', 'code', 'policy.invalid',
            'message', 'backoff or lease bounds are invalid');
    END IF;

    v_delays := ARRAY(
        SELECT LEAST(p_base_ms * (1 << (g - 1)), p_max_ms)
        FROM generate_series(1, p_max_attempts - 1) AS g);

    INSERT INTO delivery.outbox_policy(
        id, max_attempts, delays_ms, base_ms, max_ms, jitter_max_ms, lease_ms)
    VALUES (true, p_max_attempts, v_delays, p_base_ms, p_max_ms, p_jitter_max_ms, p_lease_ms)
    ON CONFLICT (id) DO UPDATE
    SET max_attempts = EXCLUDED.max_attempts,
        delays_ms = EXCLUDED.delays_ms,
        base_ms = EXCLUDED.base_ms,
        max_ms = EXCLUDED.max_ms,
        jitter_max_ms = EXCLUDED.jitter_max_ms,
        lease_ms = EXCLUDED.lease_ms;

    RETURN jsonb_build_object(
        'status', 'ok',
        'maxAttempts', p_max_attempts,
        'delaysMs', to_jsonb(v_delays),
        'leaseMs', p_lease_ms,
        'jitterMaxMs', p_jitter_max_ms);
END;
$$;

-- ---------------------------------------------------------------------------
-- Fencing: claim takes a short lease, completion is a conditional update.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION delivery.claim_outbox(p_owner text, p_limit integer)
RETURNS TABLE (
    outbox_id uuid,
    lease_version bigint,
    external_request_id text,
    correlation_id uuid,
    amount text,
    currency text
)
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, delivery
AS $$
DECLARE
    v_lease_ms integer;
BEGIN
    IF p_owner IS NULL OR p_owner = '' THEN
        RAISE EXCEPTION 'delivery.claim_invalid: owner is required';
    END IF;
    IF p_limit < 1 OR p_limit > 100 THEN
        RAISE EXCEPTION 'delivery.claim_invalid: invalid batch size';
    END IF;

    SELECT lease_ms INTO v_lease_ms FROM delivery.outbox_policy WHERE id;
    v_lease_ms := COALESCE(v_lease_ms, 2000);

    RETURN QUERY
    WITH claimed AS (
        SELECT o.outbox_id
        FROM delivery.outbox o
        WHERE (o.state IN ('PENDING','RETRY_WAIT')
               AND (o.next_attempt_at IS NULL OR o.next_attempt_at <= clock_timestamp()))
           OR (o.state = 'LEASED' AND o.lease_until < clock_timestamp())
        ORDER BY o.created_at, o.outbox_id
        FOR UPDATE SKIP LOCKED
        LIMIT p_limit
    )
    UPDATE delivery.outbox o
    SET state = 'LEASED',
        lease_owner = p_owner,
        lease_version = o.lease_version + 1,
        lease_until = clock_timestamp() + make_interval(secs => v_lease_ms / 1000.0),
        attempt_count = o.attempt_count + 1,
        next_attempt_at = NULL
    FROM claimed c
    WHERE o.outbox_id = c.outbox_id
    RETURNING o.outbox_id, o.lease_version, o.external_request_id, o.correlation_id,
        (SELECT er.amount::text FROM delivery.external_request er
          WHERE er.external_request_id = o.external_request_id),
        (SELECT er.currency FROM delivery.external_request er
          WHERE er.external_request_id = o.external_request_id);
END;
$$;

CREATE OR REPLACE FUNCTION delivery.succeed_outbox(
    p_outbox_id uuid, p_owner text, p_lease_version bigint, p_provider_payment_id text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, delivery
AS $$
DECLARE
    v_external text;
    v_updated integer;
BEGIN
    UPDATE delivery.outbox
    SET state = 'DELIVERED', lease_owner = NULL, lease_until = NULL,
        delivered_at = clock_timestamp()
    WHERE outbox_id = p_outbox_id
      AND lease_owner = p_owner
      AND lease_version = p_lease_version
      AND state = 'LEASED'
    RETURNING external_request_id INTO v_external;

    GET DIAGNOSTICS v_updated = ROW_COUNT;
    IF v_updated = 0 OR v_external IS NULL THEN
        RETURN jsonb_build_object('status', 'stale');
    END IF;

    UPDATE delivery.external_request
    SET state = 'SENT'
    WHERE external_request_id = v_external AND state = 'CREATED';

    RETURN jsonb_build_object(
        'status', 'delivered', 'providerPaymentId', p_provider_payment_id);
END;
$$;

CREATE OR REPLACE FUNCTION delivery.fail_outbox(
    p_outbox_id uuid, p_owner text, p_lease_version bigint, p_error_code text)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, delivery
AS $$
DECLARE
    v_attempts integer;
    v_updated integer;
    v_max integer;
    v_base integer;
    v_cap integer;
    v_jitter integer;
    v_retryable boolean;
    v_delay integer;
BEGIN
    -- Only transport faults, timeouts and 408/429/5xx are worth another attempt.
    v_retryable := COALESCE(p_error_code, '') LIKE '%.retryable';

    SELECT attempt_count INTO v_attempts
    FROM delivery.outbox
    WHERE outbox_id = p_outbox_id
      AND lease_owner = p_owner
      AND lease_version = p_lease_version
      AND state = 'LEASED';
    IF NOT FOUND THEN
        RETURN jsonb_build_object('status', 'stale');
    END IF;

    SELECT max_attempts, base_ms, max_ms, jitter_max_ms
      INTO v_max, v_base, v_cap, v_jitter
    FROM delivery.outbox_policy WHERE id;
    v_max := COALESCE(v_max, 4);
    v_base := COALESCE(v_base, 200);
    v_cap := COALESCE(v_cap, 800);
    v_jitter := COALESCE(v_jitter, 0);

    IF v_retryable AND v_attempts < v_max THEN
        v_delay := LEAST(v_base * (1 << GREATEST(v_attempts - 1, 0)), v_cap)
                 + floor(random() * (v_jitter + 1))::integer;
        UPDATE delivery.outbox
        SET state = 'RETRY_WAIT', lease_owner = NULL, lease_until = NULL, dead_at = NULL,
            next_attempt_at = clock_timestamp() + make_interval(secs => v_delay / 1000.0),
            last_error_code = p_error_code
        WHERE outbox_id = p_outbox_id
          AND lease_owner = p_owner
          AND lease_version = p_lease_version
          AND state = 'LEASED';
        GET DIAGNOSTICS v_updated = ROW_COUNT;
        IF v_updated = 0 THEN
            RETURN jsonb_build_object('status', 'stale');
        END IF;
        RETURN jsonb_build_object(
            'status', 'scheduled', 'attempt', v_attempts, 'delayMs', v_delay);
    END IF;

    -- DEAD stops delivery attempts only. The business outcome stays unknown.
    UPDATE delivery.outbox
    SET state = 'DEAD', lease_owner = NULL, lease_until = NULL, next_attempt_at = NULL,
        last_error_code = p_error_code, dead_at = clock_timestamp()
    WHERE outbox_id = p_outbox_id
      AND lease_owner = p_owner
      AND lease_version = p_lease_version
      AND state = 'LEASED';
    GET DIAGNOSTICS v_updated = ROW_COUNT;
    IF v_updated = 0 THEN
        RETURN jsonb_build_object('status', 'stale');
    END IF;

    RETURN jsonb_build_object(
        'status', 'dead', 'attempt', v_attempts, 'errorCode', p_error_code);
END;
$$;

-- ---------------------------------------------------------------------------
-- Stable projection: fencing columns and the terminal timestamp.
-- ---------------------------------------------------------------------------

DROP VIEW IF EXISTS autocheck.outbox;
CREATE VIEW autocheck.outbox AS
SELECT outbox_id::uuid, external_request_id::text, state::text, attempt_count::integer,
       lease_owner::text, lease_version::bigint, lease_until::timestamptz,
       next_attempt_at::timestamptz, last_error_code::text,
       created_at::timestamptz, delivered_at::timestamptz, dead_at::timestamptz
FROM delivery.outbox;

ALTER VIEW autocheck.outbox OWNER TO course_owner;
GRANT SELECT ON autocheck.outbox TO course_runtime;

-- Queue age is only observable when the job keeps its creation timestamp.
CREATE OR REPLACE VIEW autocheck.jobs AS
SELECT job_id::uuid, process_id::uuid, step_instance_id::uuid, execution_id::uuid,
       state::text, lease_owner::text, lease_version::bigint, lease_until::timestamptz,
       attempt_count::int, next_attempt_at::timestamptz, created_at::timestamptz
FROM workflow.workflow_job;

ALTER VIEW autocheck.jobs OWNER TO course_owner;
GRANT SELECT ON autocheck.jobs TO course_runtime;

GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions,
    autocheck.action_dispatches, autocheck.operations, autocheck.operation_events,
    autocheck.flow_versions, autocheck.processes, autocheck.steps, autocheck.jobs,
    autocheck.attempts, autocheck.signals, autocheck.workflow_events,
    autocheck.external_requests, autocheck.receipts, autocheck.outbox,
    autocheck.inbox, autocheck.decisions
TO autocheck_reader;

-- ---------------------------------------------------------------------------
-- Diagnostics helpers.
-- ---------------------------------------------------------------------------

CREATE OR REPLACE FUNCTION diagnostics._ts(p_ts timestamptz)
RETURNS text
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $$
    SELECT CASE
        WHEN p_ts IS NULL THEN NULL
        ELSE to_char(p_ts AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"')
    END;
$$;

CREATE OR REPLACE FUNCTION diagnostics._error(
    p_code text, p_message text, p_correlation text)
RETURNS jsonb
LANGUAGE sql
STABLE
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message,
        'retryable', false,
        'details', '{}'::jsonb,
        'meta', jsonb_build_object(
            'correlationId', p_correlation, 'actionVersion', 1));
$$;

-- One end-to-end projection assembled from database relations, never from logs.
CREATE OR REPLACE FUNCTION diagnostics.trace_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_id text := p_payload ->> 'identifier';
    v_matched text[] := ARRAY[]::text[];
    v_ids text[] := ARRAY[]::text[];
    v_fingerprint text := '';
    v_t text[];
BEGIN
    IF v_id IS NULL OR v_id = '' OR length(v_id) > 200 THEN
        RETURN diagnostics._error(
            'payload.invalid', 'identifier is required', v_correlation);
    END IF;

    IF EXISTS (SELECT 1 FROM payment.operations WHERE operation_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'operationId');
    END IF;
    IF EXISTS (SELECT 1 FROM workflow.process_instance WHERE process_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'processId');
    END IF;
    IF EXISTS (SELECT 1 FROM workflow.step_instance WHERE step_instance_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'stepInstanceId');
    END IF;
    IF EXISTS (SELECT 1 FROM workflow.workflow_job WHERE job_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'jobId');
    END IF;
    IF EXISTS (SELECT 1 FROM workflow.workflow_job WHERE execution_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'executionId');
    END IF;
    IF EXISTS (SELECT 1 FROM workflow.task_attempt WHERE attempt_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'attemptId');
    END IF;
    IF EXISTS (SELECT 1 FROM payment.decisions WHERE decision_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'decisionId');
    END IF;
    IF EXISTS (SELECT 1 FROM api.action_dispatches WHERE correlation_id::text = v_id) THEN
        v_matched := array_append(v_matched, 'correlationId');
    END IF;
    IF EXISTS (SELECT 1 FROM delivery.external_request
               WHERE external_request_id = v_id) THEN
        v_matched := array_append(v_matched, 'externalRequestId');
    END IF;
    IF EXISTS (SELECT 1 FROM delivery.inbox WHERE message_id = v_id)
       OR EXISTS (SELECT 1 FROM delivery.receipt WHERE message_id = v_id) THEN
        v_matched := array_append(v_matched, 'messageId');
    END IF;
    IF EXISTS (SELECT 1 FROM payment.operations WHERE request_id = v_id)
       OR EXISTS (SELECT 1 FROM api.action_dispatches WHERE request_id = v_id) THEN
        v_matched := array_append(v_matched, 'requestId');
    END IF;

    IF cardinality(v_matched) = 0 THEN
        RETURN diagnostics._error(
            'diagnostics.trace_not_found', 'no records match the identifier', v_correlation);
    END IF;

    v_ids := ARRAY[v_id];

    -- Walk the relation graph to a fixed point so one identifier yields one trace.
    -- Each statement adds the outgoing edges of one relation group and immediately
    -- folds them into v_ids, so a single pass advances the whole chain.
    FOR i IN 1..8 LOOP
        v_fingerprint := COALESCE((
            SELECT string_agg(x, ',' ORDER BY x) FROM unnest(v_ids) AS s(x)), '');

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT o.request_id AS x FROM payment.operations o
            WHERE o.operation_id::text = ANY(v_ids) AND o.request_id IS NOT NULL
            UNION
            SELECT o.request_id FROM payment.operations o
            WHERE o.process_id::text = ANY(v_ids) AND o.request_id IS NOT NULL
            UNION
            SELECT o.process_id::text FROM payment.operations o
            WHERE o.operation_id::text = ANY(v_ids) AND o.process_id IS NOT NULL
            UNION
            SELECT o.operation_id::text FROM payment.operations o
            WHERE o.process_id::text = ANY(v_ids) OR o.request_id = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT d.request_id AS x FROM api.action_dispatches d
            WHERE d.correlation_id::text = ANY(v_ids)
            UNION
            SELECT d.correlation_id::text FROM api.action_dispatches d
            WHERE d.request_id = ANY(v_ids) OR d.correlation_id::text = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT s.step_instance_id::text AS x FROM workflow.step_instance s
            WHERE s.process_id::text = ANY(v_ids)
            UNION
            SELECT s.process_id::text FROM workflow.step_instance s
            WHERE s.step_instance_id::text = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT j.job_id::text AS x FROM workflow.workflow_job j
            WHERE j.process_id::text = ANY(v_ids)
               OR j.step_instance_id::text = ANY(v_ids)
            UNION
            SELECT j.execution_id::text FROM workflow.workflow_job j
            WHERE j.process_id::text = ANY(v_ids)
               OR j.step_instance_id::text = ANY(v_ids)
            UNION
            SELECT j.step_instance_id::text FROM workflow.workflow_job j
            WHERE j.process_id::text = ANY(v_ids)
               OR j.step_instance_id::text = ANY(v_ids)
               OR j.job_id::text = ANY(v_ids)
               OR j.execution_id::text = ANY(v_ids)
            UNION
            SELECT j.process_id::text FROM workflow.workflow_job j
            WHERE j.job_id::text = ANY(v_ids) OR j.execution_id::text = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT a.attempt_id::text AS x FROM workflow.task_attempt a
            WHERE a.job_id::text = ANY(v_ids) OR a.execution_id::text = ANY(v_ids)
            UNION
            SELECT a.job_id::text FROM workflow.task_attempt a
            WHERE a.attempt_id::text = ANY(v_ids) OR a.job_id::text = ANY(v_ids)
            UNION
            SELECT a.execution_id::text FROM workflow.task_attempt a
            WHERE a.attempt_id::text = ANY(v_ids) OR a.job_id::text = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT er.external_request_id AS x FROM delivery.external_request er
            WHERE er.operation_id::text = ANY(v_ids) OR er.correlation_id::text = ANY(v_ids)
            UNION
            SELECT er.operation_id::text FROM delivery.external_request er
            WHERE er.external_request_id = ANY(v_ids) AND er.operation_id IS NOT NULL
            UNION
            SELECT er.correlation_id::text FROM delivery.external_request er
            WHERE er.external_request_id = ANY(v_ids) AND er.correlation_id IS NOT NULL
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT i.message_id AS x FROM delivery.inbox i
            WHERE i.external_request_id = ANY(v_ids) OR i.process_id::text = ANY(v_ids)
            UNION
            SELECT i.external_request_id FROM delivery.inbox i
            WHERE i.message_id = ANY(v_ids) OR i.external_request_id = ANY(v_ids)
            UNION
            SELECT i.process_id::text FROM delivery.inbox i
            WHERE i.message_id = ANY(v_ids) OR i.external_request_id = ANY(v_ids)
            UNION
            SELECT b.outbox_id::text FROM delivery.outbox b
            WHERE b.external_request_id = ANY(v_ids)
            UNION
            SELECT r.message_id FROM delivery.receipt r
            WHERE r.external_request_id = ANY(v_ids) OR r.message_id = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        SELECT COALESCE(array_agg(DISTINCT e.x), ARRAY[]::text[]) INTO v_t FROM (
            SELECT d.decision_id::text AS x FROM payment.decisions d
            WHERE d.process_id::text = ANY(v_ids) OR d.step_instance_id::text = ANY(v_ids)
            UNION
            SELECT d.step_instance_id::text FROM payment.decisions d
            WHERE d.decision_id::text = ANY(v_ids)
               OR d.process_id::text = ANY(v_ids)
               OR d.step_instance_id::text = ANY(v_ids)
            UNION
            SELECT d.process_id::text FROM payment.decisions d
            WHERE d.decision_id::text = ANY(v_ids) OR d.process_id::text = ANY(v_ids)
        ) e;
        v_ids := ARRAY(SELECT DISTINCT x FROM unnest(v_ids || v_t) AS s(x) WHERE x IS NOT NULL);

        EXIT WHEN v_fingerprint = COALESCE((
            SELECT string_agg(x, ',' ORDER BY x) FROM unnest(v_ids) AS s(x)), '');
    END LOOP;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object(
            'query', jsonb_build_object(
                'identifier', v_id, 'matchedBy', to_jsonb(v_matched)),
            'dispatches', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'correlationId', d.correlation_id::text,
                        'requestId', d.request_id,
                        'module', d.module,
                        'action', d.action,
                        'version', d.version,
                        'status', d.status,
                        'outcome', d.outcome,
                        'occurredAt', diagnostics._ts(d.occurred_at))
                    ORDER BY d.occurred_at, d.request_id)
                FROM api.action_dispatches d
                WHERE d.correlation_id::text = ANY(v_ids) OR d.request_id = ANY(v_ids)
            ), '[]'::jsonb),
            'operation', (
                SELECT jsonb_build_object(
                        'operationId', o.operation_id::text,
                        'requestId', o.request_id,
                        'operationKind', o.operation_kind,
                        'amount', o.amount::text,
                        'currency', o.currency,
                        'status', o.status,
                        'processId', o.process_id::text,
                        'createdAt', diagnostics._ts(o.created_at),
                        'updatedAt', diagnostics._ts(o.updated_at))
                FROM payment.operations o
                WHERE o.operation_id::text = ANY(v_ids)
                ORDER BY o.created_at, o.operation_id
                LIMIT 1
            ),
            'operationEvents', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'eventId', e.event_id::text,
                        'eventType', e.event_type,
                        'occurredAt', diagnostics._ts(e.occurred_at))
                    ORDER BY e.occurred_at, e.event_id)
                FROM payment.operation_events e
                WHERE e.operation_id::text = ANY(v_ids)
            ), '[]'::jsonb),
            'process', (
                SELECT jsonb_build_object(
                        'processId', p.process_id::text,
                        'flowName', p.flow_name,
                        'flowVersion', p.flow_version,
                        'state', p.state,
                        'currentStepKey', p.current_step_key,
                        'createdAt', diagnostics._ts(p.created_at),
                        'updatedAt', diagnostics._ts(p.updated_at))
                FROM workflow.process_instance p
                WHERE p.process_id::text = ANY(v_ids)
                ORDER BY p.created_at, p.process_id
                LIMIT 1
            ),
            'steps', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'stepInstanceId', s.step_instance_id::text,
                        'stepKey', s.step_key,
                        'stepType', s.step_type,
                        'state', s.state,
                        'outcome', s.outcome,
                        'enteredAt', diagnostics._ts(s.entered_at),
                        'completedAt', diagnostics._ts(s.completed_at))
                    ORDER BY s.entered_at, s.step_instance_id)
                FROM workflow.step_instance s
                WHERE s.process_id::text = ANY(v_ids)
                   OR s.step_instance_id::text = ANY(v_ids)
            ), '[]'::jsonb),
            'jobs', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'jobId', j.job_id::text,
                        'stepInstanceId', j.step_instance_id::text,
                        'executionId', j.execution_id::text,
                        'state', j.state,
                        'leaseVersion', j.lease_version,
                        'attemptCount', j.attempt_count,
                        'nextAttemptAt', diagnostics._ts(j.next_attempt_at))
                    ORDER BY j.created_at, j.job_id)
                FROM workflow.workflow_job j
                WHERE j.process_id::text = ANY(v_ids)
                   OR j.job_id::text = ANY(v_ids)
                   OR j.execution_id::text = ANY(v_ids)
            ), '[]'::jsonb),
            'attempts', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'attemptId', a.attempt_id::text,
                        'jobId', a.job_id::text,
                        'executionId', a.execution_id::text,
                        'leaseVersion', a.lease_version,
                        'attemptNumber', a.attempt_number,
                        'status', a.status,
                        'outcome', a.outcome,
                        'errorCode', a.error_code,
                        'startedAt', diagnostics._ts(a.started_at),
                        'finishedAt', diagnostics._ts(a.finished_at))
                    ORDER BY a.started_at, a.attempt_id)
                FROM workflow.task_attempt a
                WHERE a.job_id::text = ANY(v_ids)
                   OR a.attempt_id::text = ANY(v_ids)
                   OR a.execution_id::text = ANY(v_ids)
            ), '[]'::jsonb),
            'outbox', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'outboxId', b.outbox_id::text,
                        'externalRequestId', b.external_request_id,
                        'state', b.state,
                        'attemptCount', b.attempt_count,
                        'leaseVersion', b.lease_version,
                        'nextAttemptAt', diagnostics._ts(b.next_attempt_at),
                        'lastErrorCode', b.last_error_code,
                        'createdAt', diagnostics._ts(b.created_at),
                        'deliveredAt', diagnostics._ts(b.delivered_at),
                        'deadAt', diagnostics._ts(b.dead_at))
                    ORDER BY b.created_at, b.outbox_id)
                FROM delivery.outbox b
                WHERE b.outbox_id::text = ANY(v_ids)
                   OR b.external_request_id = ANY(v_ids)
            ), '[]'::jsonb),
            'inbox', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'messageId', i.message_id,
                        'state', i.state,
                        'receivedAt', diagnostics._ts(i.received_at),
                        'appliedAt', diagnostics._ts(i.applied_at))
                    ORDER BY i.received_at, i.message_id)
                FROM delivery.inbox i
                WHERE i.message_id = ANY(v_ids)
                   OR i.external_request_id = ANY(v_ids)
            ), '[]'::jsonb),
            'receipts', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'messageId', r.message_id,
                        'externalRequestId', r.external_request_id,
                        'outcome', r.outcome,
                        'signatureValid', r.signature_valid,
                        'receivedAt', diagnostics._ts(r.received_at),
                        'appliedAt', diagnostics._ts(r.applied_at))
                    ORDER BY r.received_at, r.message_id)
                FROM delivery.receipt r
                WHERE r.message_id = ANY(v_ids)
                   OR r.external_request_id = ANY(v_ids)
            ), '[]'::jsonb),
            'decisions', COALESCE((
                SELECT jsonb_agg(jsonb_build_object(
                        'decisionId', d.decision_id::text,
                        'stepInstanceId', d.step_instance_id::text,
                        'source', d.source,
                        'principal', d.principal,
                        'outcome', d.outcome,
                        'ruleVersion', d.rule_version,
                        'createdAt', diagnostics._ts(d.created_at))
                    ORDER BY d.created_at, d.decision_id)
                FROM payment.decisions d
                WHERE d.decision_id::text = ANY(v_ids)
                   OR d.process_id::text = ANY(v_ids)
            ), '[]'::jsonb)
        ),
        'meta', jsonb_build_object(
            'correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

-- Operations that exhausted automatic delivery attempts, have waited at least
-- v_stalled_after, and still wait for a receipt. The operation leaves the sample
-- once the receipt is applied, because the WAIT_SIGNAL step is no longer WAITING.
CREATE OR REPLACE FUNCTION diagnostics.stalled_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_items jsonb;
    -- A stalled wait is not merely the DEAD state: automatic delivery has to have
    -- been exhausted for at least this threshold before the operation is reported.
    -- The age is read from the stored dead_at, so the sample depends only on
    -- persisted facts and never on what the caller happens to observe.
    v_stalled_after CONSTANT interval := interval '10 seconds';
BEGIN
    SELECT COALESCE(
        jsonb_agg(item ORDER BY (item ->> 'operationId')), '[]'::jsonb)
    INTO v_items
    FROM (
        SELECT DISTINCT ON (o.operation_id)
            jsonb_build_object(
                'operationId', o.operation_id::text,
                'processId', p.process_id::text,
                'externalRequestId', b.external_request_id) AS item
        FROM delivery.outbox b
        JOIN delivery.external_request er
          ON er.external_request_id = b.external_request_id
        JOIN payment.operations o
          ON o.operation_id = er.operation_id
        JOIN workflow.process_instance p
          ON p.process_id = o.process_id
        JOIN workflow.step_instance s
          ON s.process_id = p.process_id
         AND s.step_type = 'WAIT_SIGNAL'
         AND s.state = 'WAITING'
        WHERE b.state = 'DEAD'
          AND clock_timestamp() - COALESCE(b.dead_at, b.created_at) >= v_stalled_after
        ORDER BY o.operation_id
    ) stalled;

    RETURN jsonb_build_object(
        'status', 'ok',
        'outcome', 'FOUND',
        'result', jsonb_build_object('items', v_items),
        'meta', jsonb_build_object(
            'correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

-- ---------------------------------------------------------------------------
-- Ownership and privileges.
-- ---------------------------------------------------------------------------

ALTER TABLE delivery.outbox_policy OWNER TO course_owner;
ALTER TABLE delivery.outbox OWNER TO course_owner;
ALTER FUNCTION delivery.apply_outbox_policy(integer,integer,integer,integer,integer)
    OWNER TO course_owner;
ALTER FUNCTION delivery.claim_outbox(text,integer) OWNER TO course_owner;
ALTER FUNCTION delivery.succeed_outbox(uuid,text,bigint,text) OWNER TO course_owner;
ALTER FUNCTION delivery.fail_outbox(uuid,text,bigint,text) OWNER TO course_owner;
ALTER FUNCTION diagnostics._ts(timestamptz) OWNER TO course_owner;
ALTER FUNCTION diagnostics._error(text,text,text) OWNER TO course_owner;
ALTER FUNCTION diagnostics.trace_v1(jsonb,jsonb) OWNER TO course_owner;
ALTER FUNCTION diagnostics.stalled_v1(jsonb,jsonb) OWNER TO course_owner;

GRANT USAGE ON SCHEMA diagnostics TO course_owner;
ALTER SCHEMA diagnostics OWNER TO course_owner;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA diagnostics FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA payment FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA training FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA api FROM PUBLIC;

-- pgcrypto lives in the api schema and is reachable only through the owner role.
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA api TO course_owner;

GRANT EXECUTE ON FUNCTION delivery.apply_outbox_policy(integer,integer,integer,integer,integer)
    TO course_migration;
GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text,integer) TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid,text,bigint,text)
    TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid,text,bigint,text)
    TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO course_owner, inbox_reconciler;
GRANT EXECUTE ON FUNCTION delivery.apply_inbox_for_process(uuid,text) TO course_owner;
GRANT EXECUTE ON FUNCTION diagnostics.trace_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION diagnostics.stalled_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION diagnostics._ts(timestamptz) TO course_owner;
GRANT EXECUTE ON FUNCTION diagnostics._error(text,text,text) TO course_owner;

-- ---------------------------------------------------------------------------
-- Catalog: diagnostics.trace and diagnostics.stalled.
-- ---------------------------------------------------------------------------

INSERT INTO api.action_catalog(
    module, action, version, http_method, target_schema, target_function,
    request_schema, response_schema, outcomes, required_policy,
    idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES ('diagnostics','trace',1,'POST','diagnostics','trace_v1',
 '{
   "$schema":"https://json-schema.org/draft/2020-12/schema",
   "type":"object","required":["identifier"],"additionalProperties":false,
   "properties":{
     "identifier":{"type":"string","minLength":1,"maxLength":200,"pattern":"^[^\\r\\n]+$"}
   }
 }'::jsonb,
 '{
   "$schema":"https://json-schema.org/draft/2020-12/schema",
   "type":"object",
   "required":["query","dispatches","operation","operationEvents","process","steps","jobs","attempts","outbox","inbox","receipts","decisions"],
   "properties":{
     "query":{
       "type":"object","required":["identifier","matchedBy"],"additionalProperties":false,
       "properties":{
         "identifier":{"type":"string","minLength":1,"maxLength":200},
         "matchedBy":{
           "type":"array","minItems":1,"uniqueItems":true,
           "items":{"enum":["correlationId","requestId","operationId","processId","stepInstanceId","jobId","executionId","attemptId","externalRequestId","messageId","decisionId"]}
         }
       }
     },
     "dispatches":{"type":"array","items":{"$ref":"#/$defs/dispatch"}},
     "operation":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/operation"}]},
     "operationEvents":{"type":"array","items":{"$ref":"#/$defs/operationEvent"}},
     "process":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/process"}]},
     "steps":{"type":"array","items":{"$ref":"#/$defs/step"}},
     "jobs":{"type":"array","items":{"$ref":"#/$defs/job"}},
     "attempts":{"type":"array","items":{"$ref":"#/$defs/attempt"}},
     "outbox":{"type":"array","items":{"$ref":"#/$defs/outbox"}},
     "inbox":{"type":"array","items":{"$ref":"#/$defs/inbox"}},
     "receipts":{"type":"array","items":{"$ref":"#/$defs/receipt"}},
     "decisions":{"type":"array","items":{"$ref":"#/$defs/decision"}}
   },
   "$defs":{
     "uuid":{"type":"string","format":"uuid"},
     "timestamp":{"type":"string","format":"date-time"},
     "nullableTimestamp":{"type":["string","null"],"format":"date-time"},
     "nullableString":{"type":["string","null"]},
     "dispatch":{
       "type":"object",
       "required":["correlationId","requestId","module","action","version","status","outcome","occurredAt"],
       "additionalProperties":false,
       "properties":{
         "correlationId":{"$ref":"#/$defs/uuid"},
         "requestId":{"type":"string"},
         "module":{"type":"string"},
         "action":{"type":"string"},
         "version":{"type":"integer","minimum":1},
         "status":{"enum":["OK","ERROR"]},
         "outcome":{"$ref":"#/$defs/nullableString"},
         "occurredAt":{"$ref":"#/$defs/timestamp"}
       }
     },
     "operation":{
       "type":"object",
       "required":["operationId","requestId","operationKind","amount","currency","status","processId","createdAt","updatedAt"],
       "additionalProperties":false,
       "properties":{
         "operationId":{"$ref":"#/$defs/uuid"},
         "requestId":{"type":"string"},
         "operationKind":{"type":"string"},
         "amount":{"type":"string","pattern":"^-?[0-9]+\\.[0-9]{2}$"},
         "currency":{"type":"string"},
         "status":{"enum":["CREATED","PROCESSING","COMPLETED","REJECTED"]},
         "processId":{"oneOf":[{"type":"null"},{"$ref":"#/$defs/uuid"}]},
         "createdAt":{"$ref":"#/$defs/timestamp"},
         "updatedAt":{"$ref":"#/$defs/timestamp"}
       }
     },
     "operationEvent":{
       "type":"object","required":["eventId","eventType","occurredAt"],"additionalProperties":false,
       "properties":{
         "eventId":{"$ref":"#/$defs/uuid"},
         "eventType":{"type":"string"},
         "occurredAt":{"$ref":"#/$defs/timestamp"}
       }
     },
     "process":{
       "type":"object",
       "required":["processId","flowName","flowVersion","state","currentStepKey","createdAt","updatedAt"],
       "additionalProperties":false,
       "properties":{
         "processId":{"$ref":"#/$defs/uuid"},
         "flowName":{"type":"string"},
         "flowVersion":{"type":"integer","minimum":1},
         "state":{"enum":["CREATED","RUNNING","WAITING_SIGNAL","WAITING_MANUAL","COMPLETED","FAILED"]},
         "currentStepKey":{"$ref":"#/$defs/nullableString"},
         "createdAt":{"$ref":"#/$defs/timestamp"},
         "updatedAt":{"$ref":"#/$defs/timestamp"}
       }
     },
     "step":{
       "type":"object",
       "required":["stepInstanceId","stepKey","stepType","state","outcome","enteredAt","completedAt"],
       "additionalProperties":false,
       "properties":{
         "stepInstanceId":{"$ref":"#/$defs/uuid"},
         "stepKey":{"type":"string"},
         "stepType":{"enum":["AUTOMATIC","WAIT_SIGNAL","MANUAL","END"]},
         "state":{"enum":["PENDING","READY","RUNNING","WAITING","COMPLETED","FAILED"]},
         "outcome":{"$ref":"#/$defs/nullableString"},
         "enteredAt":{"$ref":"#/$defs/timestamp"},
         "completedAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "job":{
       "type":"object",
       "required":["jobId","stepInstanceId","executionId","state","leaseVersion","attemptCount","nextAttemptAt"],
       "additionalProperties":false,
       "properties":{
         "jobId":{"$ref":"#/$defs/uuid"},
         "stepInstanceId":{"$ref":"#/$defs/uuid"},
         "executionId":{"$ref":"#/$defs/uuid"},
         "state":{"enum":["READY","LEASED","RETRY_WAIT","SUCCEEDED","DEAD"]},
         "leaseVersion":{"type":"integer","minimum":0},
         "attemptCount":{"type":"integer","minimum":0},
         "nextAttemptAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "attempt":{
       "type":"object",
       "required":["attemptId","jobId","executionId","leaseVersion","attemptNumber","status","outcome","errorCode","startedAt","finishedAt"],
       "additionalProperties":false,
       "properties":{
         "attemptId":{"$ref":"#/$defs/uuid"},
         "jobId":{"$ref":"#/$defs/uuid"},
         "executionId":{"$ref":"#/$defs/uuid"},
         "leaseVersion":{"type":"integer","minimum":1},
         "attemptNumber":{"type":"integer","minimum":1},
         "status":{"enum":["RUNNING","SUCCEEDED","FAILED","STALE"]},
         "outcome":{"$ref":"#/$defs/nullableString"},
         "errorCode":{"$ref":"#/$defs/nullableString"},
         "startedAt":{"$ref":"#/$defs/timestamp"},
         "finishedAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "outbox":{
       "type":"object",
       "required":["outboxId","externalRequestId","state","attemptCount","leaseVersion","nextAttemptAt","lastErrorCode","createdAt","deliveredAt","deadAt"],
       "additionalProperties":false,
       "properties":{
         "outboxId":{"$ref":"#/$defs/uuid"},
         "externalRequestId":{"type":"string"},
         "state":{"enum":["PENDING","LEASED","RETRY_WAIT","DELIVERED","DEAD","CONFIRMED"]},
         "attemptCount":{"type":"integer","minimum":0},
         "leaseVersion":{"type":"integer","minimum":0},
         "nextAttemptAt":{"$ref":"#/$defs/nullableTimestamp"},
         "lastErrorCode":{"$ref":"#/$defs/nullableString"},
         "createdAt":{"$ref":"#/$defs/timestamp"},
         "deliveredAt":{"$ref":"#/$defs/nullableTimestamp"},
         "deadAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "inbox":{
       "type":"object","required":["messageId","state","receivedAt","appliedAt"],"additionalProperties":false,
       "properties":{
         "messageId":{"type":"string"},
         "state":{"enum":["RECEIVED","APPLIED","CONFLICT"]},
         "receivedAt":{"$ref":"#/$defs/timestamp"},
         "appliedAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "receipt":{
       "type":"object",
       "required":["messageId","externalRequestId","outcome","signatureValid","receivedAt","appliedAt"],
       "additionalProperties":false,
       "properties":{
         "messageId":{"type":"string"},
         "externalRequestId":{"type":"string"},
         "outcome":{"enum":["COMPLETED","REJECTED"]},
         "signatureValid":{"const":true},
         "receivedAt":{"$ref":"#/$defs/timestamp"},
         "appliedAt":{"$ref":"#/$defs/nullableTimestamp"}
       }
     },
     "decision":{
       "type":"object",
       "required":["decisionId","stepInstanceId","source","principal","outcome","ruleVersion","createdAt"],
       "additionalProperties":false,
       "properties":{
         "decisionId":{"$ref":"#/$defs/uuid"},
         "stepInstanceId":{"$ref":"#/$defs/uuid"},
         "source":{"enum":["LIMIT_RULE","MANUAL"]},
         "principal":{"type":"string"},
         "outcome":{"enum":["APPROVED","REJECTED"]},
         "ruleVersion":{"$ref":"#/$defs/nullableString"},
         "createdAt":{"$ref":"#/$defs/timestamp"}
       }
     }
   },
   "additionalProperties":false
 }'::jsonb,
 '["FOUND"]'::jsonb,
 '["diagnostics:read"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('diagnostics','stalled',1,'POST','diagnostics','stalled_v1',
 '{
   "$schema":"https://json-schema.org/draft/2020-12/schema",
   "type":"object","properties":{},"additionalProperties":false
 }'::jsonb,
 '{
   "$schema":"https://json-schema.org/draft/2020-12/schema",
   "type":"object","required":["items"],"additionalProperties":false,
   "properties":{
     "items":{
       "type":"array","uniqueItems":true,
       "items":{
         "type":"object",
         "required":["operationId","processId","externalRequestId"],
         "additionalProperties":false,
         "properties":{
           "operationId":{"type":"string","format":"uuid"},
           "processId":{"type":"string","format":"uuid"},
           "externalRequestId":{"type":"string","minLength":1,"maxLength":200,"not":{"pattern":"[\r\n]"}}
         }
       }
     }
   }
 }'::jsonb,
 '["FOUND"]'::jsonb,
 '["diagnostics:read"]'::jsonb,
 'none','none',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;
