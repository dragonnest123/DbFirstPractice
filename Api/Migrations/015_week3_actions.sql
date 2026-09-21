-- Week 3: delivery SQL boundary, payment/review actions, workflow.manual, catalog.

-- Retry policy for outbox (test profile values; week 4 moves to configurable runtime).
CREATE TABLE IF NOT EXISTS delivery.outbox_policy (
    id boolean PRIMARY KEY DEFAULT true CHECK (id),
    max_attempts integer NOT NULL,
    delays_ms integer[] NOT NULL
);
INSERT INTO delivery.outbox_policy(id, max_attempts, delays_ms) VALUES (true, 3, ARRAY[200,400,800])
ON CONFLICT (id) DO NOTHING;

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
BEGIN
    IF p_owner IS NULL OR p_owner = '' THEN
        RAISE EXCEPTION 'delivery.claim_invalid: owner is required';
    END IF;
    IF p_limit < 1 OR p_limit > 100 THEN
        RAISE EXCEPTION 'delivery.claim_invalid: invalid batch size';
    END IF;

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
        lease_until = clock_timestamp() + interval '30 seconds',
        attempt_count = o.attempt_count + 1,
        next_attempt_at = NULL
    FROM claimed c
    WHERE o.outbox_id = c.outbox_id
    RETURNING o.outbox_id, o.lease_version, o.external_request_id, o.correlation_id,
        (SELECT er.amount::text FROM delivery.external_request er WHERE er.external_request_id = o.external_request_id),
        (SELECT er.currency FROM delivery.external_request er WHERE er.external_request_id = o.external_request_id);
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
    SET state = 'DELIVERED', lease_owner = NULL, lease_until = NULL, delivered_at = clock_timestamp()
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

    RETURN jsonb_build_object('status', 'delivered', 'providerPaymentId', p_provider_payment_id);
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
    v_external text;
    v_attempts integer;
    v_max integer;
    v_delay integer;
    v_retryable boolean;
BEGIN
    SELECT external_request_id, attempt_count INTO v_external, v_attempts
    FROM delivery.outbox
    WHERE outbox_id = p_outbox_id
      AND lease_owner = p_owner
      AND lease_version = p_lease_version
      AND state = 'LEASED';

    IF v_external IS NULL THEN
        RETURN jsonb_build_object('status', 'stale');
    END IF;

    v_retryable := p_error_code LIKE '%.retryable';
    IF v_retryable THEN
        SELECT max_attempts, COALESCE(delays_ms[v_attempts], 1000) INTO v_max, v_delay
        FROM delivery.outbox_policy;
        IF v_attempts < v_max THEN
            UPDATE delivery.outbox
            SET state = 'RETRY_WAIT', lease_owner = NULL, lease_until = NULL,
                next_attempt_at = clock_timestamp() + make_interval(secs => v_delay / 1000.0),
                last_error_code = p_error_code
            WHERE outbox_id = p_outbox_id;
            RETURN jsonb_build_object('status', 'scheduled', 'nextAttemptAt', v_delay);
        END IF;
    END IF;

    UPDATE delivery.outbox
    SET state = 'DEAD', lease_owner = NULL, lease_until = NULL, last_error_code = p_error_code
    WHERE outbox_id = p_outbox_id;

    RETURN jsonb_build_object('status', 'dead', 'errorCode', p_error_code);
END;
$$;

CREATE OR REPLACE FUNCTION delivery.apply_inbox_for_process(p_process_id uuid, p_signal_type text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, delivery
AS $$
DECLARE
    r record;
BEGIN
    FOR r IN
        SELECT i.message_id, i.payload
        FROM delivery.inbox i
        WHERE i.process_id = p_process_id
          AND i.signal_type = p_signal_type
          AND i.state = 'RECEIVED'
        ORDER BY i.received_at, i.message_id
        LIMIT 1
    LOOP
        PERFORM workflow.accept_signal(p_process_id, p_signal_type, r.message_id, r.payload);
        UPDATE delivery.inbox
        SET state = 'APPLIED', applied_at = clock_timestamp()
        WHERE message_id = r.message_id;
        UPDATE delivery.receipt
        SET applied_at = clock_timestamp()
        WHERE message_id = r.message_id;
    END LOOP;
END;
$$;

CREATE OR REPLACE FUNCTION delivery.reconcile_inbox(p_limit integer)
RETURNS integer
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, delivery
AS $$
DECLARE
    v_count integer := 0;
    r record;
BEGIN
    IF p_limit < 1 OR p_limit > 1000 THEN
        RAISE EXCEPTION 'delivery.reconcile_invalid: invalid limit';
    END IF;

    FOR r IN
        SELECT i.message_id, i.process_id, i.signal_type, i.payload
        FROM delivery.inbox i
        JOIN workflow.process_instance p ON p.process_id = i.process_id
        JOIN workflow.step_instance s ON s.process_id = i.process_id
            AND s.step_type = 'WAIT_SIGNAL' AND s.state = 'WAITING'
        JOIN workflow.step_definition d ON d.flow_name = p.flow_name
            AND d.flow_version = p.flow_version AND d.step_key = s.step_key
        WHERE i.state = 'RECEIVED'
          AND d.params ->> 'signal_type' = i.signal_type
        ORDER BY i.received_at, i.message_id
        FOR UPDATE OF i SKIP LOCKED
        LIMIT p_limit
    LOOP
        BEGIN
            PERFORM workflow.accept_signal(r.process_id, r.signal_type, r.message_id, r.payload);
            UPDATE delivery.inbox
            SET state = 'APPLIED', applied_at = clock_timestamp()
            WHERE message_id = r.message_id;
            UPDATE delivery.receipt
            SET applied_at = clock_timestamp()
            WHERE message_id = r.message_id;
            v_count := v_count + 1;
        EXCEPTION WHEN OTHERS THEN
            NULL;
        END;
    END LOOP;

    RETURN v_count;
END;
$$;

-- Generic WAIT_SIGNAL entry also applies already-saved Inbox (early receipt).
CREATE OR REPLACE FUNCTION workflow._enter_step(p_process_id uuid, p_step_key text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_proc workflow.process_instance%ROWTYPE;
    v_step workflow.step_definition%ROWTYPE;
    v_step_instance_id uuid;
    v_outcome text;
BEGIN
    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = p_process_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.process_not_found: process % does not exist', p_process_id;
    END IF;

    SELECT * INTO v_step
    FROM workflow.step_definition
    WHERE flow_name = v_proc.flow_name
      AND flow_version = v_proc.flow_version
      AND step_key = p_step_key;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.unknown_step: step % is not in pinned map', p_step_key;
    END IF;

    INSERT INTO workflow.step_instance(process_id, step_key, step_type, state)
    VALUES (p_process_id, v_step.step_key, v_step.step_type, 'PENDING')
    RETURNING step_instance_id INTO v_step_instance_id;

    UPDATE workflow.process_instance
    SET current_step_key = v_step.step_key
    WHERE process_id = p_process_id;

    IF v_step.step_type = 'AUTOMATIC' THEN
        UPDATE workflow.step_instance SET state = 'READY' WHERE step_instance_id = v_step_instance_id;
        INSERT INTO workflow.workflow_job(process_id, step_instance_id, execution_id, state)
        VALUES (p_process_id, v_step_instance_id, gen_random_uuid(), 'READY');
        UPDATE workflow.process_instance SET state = 'RUNNING' WHERE process_id = p_process_id;
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'StepEntered');
    ELSIF v_step.step_type = 'WAIT_SIGNAL' THEN
        UPDATE workflow.step_instance SET state = 'WAITING' WHERE step_instance_id = v_step_instance_id;
        UPDATE workflow.process_instance SET state = 'WAITING_SIGNAL' WHERE process_id = p_process_id;
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'StepEntered');
        PERFORM workflow._apply_ready_signals(p_process_id, v_step_instance_id, v_step.params);
        PERFORM delivery.apply_inbox_for_process(p_process_id, v_step.params ->> 'signal_type');
    ELSIF v_step.step_type = 'MANUAL' THEN
        UPDATE workflow.step_instance SET state = 'WAITING' WHERE step_instance_id = v_step_instance_id;
        UPDATE workflow.process_instance SET state = 'WAITING_MANUAL' WHERE process_id = p_process_id;
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'StepEntered');
    ELSIF v_step.step_type = 'END' THEN
        v_outcome := v_step.params ->> 'outcome';
        UPDATE workflow.step_instance
        SET state = 'COMPLETED', outcome = v_outcome, completed_at = clock_timestamp()
        WHERE step_instance_id = v_step_instance_id;
        UPDATE workflow.process_instance SET state = 'COMPLETED' WHERE process_id = p_process_id;
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'StepEntered');
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'StepCompleted');
        INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
        VALUES (p_process_id, v_step_instance_id, 'ProcessCompleted');
    END IF;
END;
$$;

-- Server-side binding: operationKind -> flow.
CREATE TABLE IF NOT EXISTS payment.flow_binding (
    operation_kind text PRIMARY KEY,
    flow_name text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);
INSERT INTO payment.flow_binding(operation_kind, flow_name)
VALUES ('PAYMENT_EXECUTION', 'payment-processing'), ('PAYMENT_APPROVAL', 'payment-review')
ON CONFLICT (operation_kind) DO NOTHING;

-- Relax operation event types.
ALTER TABLE payment.operation_events DROP CONSTRAINT IF EXISTS operation_events_event_type_check;
ALTER TABLE payment.operation_events
    ADD CONSTRAINT operation_events_event_type_check
    CHECK (event_type IN ('OPERATION_CREATED','OPERATION_SUBMITTED','OPERATION_COMPLETED','OPERATION_REJECTED'));

-- Domain error helper for action targets.
CREATE OR REPLACE FUNCTION payment._domain_error(p_code text, p_message text, p_correlation text)
RETURNS jsonb
LANGUAGE sql
SET search_path = pg_catalog
AS $$
    SELECT jsonb_build_object(
        'status', 'error',
        'code', p_code,
        'message', p_message,
        'retryable', false,
        'details', '{}'::jsonb,
        'meta', jsonb_build_object('correlationId', p_correlation, 'actionVersion', 1)
    );
$$;

-- Status transition executed as course_owner so course_target (and course_migration)
-- never get direct DML on payment.operations/operation_events.
CREATE OR REPLACE FUNCTION payment._set_status(
    p_op_id uuid, p_status text, p_event text, p_payload_hash text, p_process_id uuid DEFAULT NULL)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
BEGIN
    UPDATE payment.operations
    SET status = p_status, process_id = COALESCE(p_process_id, process_id)
    WHERE operation_id = p_op_id;
    INSERT INTO payment.operation_events(event_id, operation_id, event_type, payload_hash)
    VALUES (gen_random_uuid(), p_op_id, p_event, p_payload_hash);
END;
$$;

CREATE OR REPLACE FUNCTION payment.submit_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, workflow, api
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op_id text := p_payload ->> 'operationId';
    v_op payment.operations%ROWTYPE;
    v_binding payment.flow_binding%ROWTYPE;
    v_start jsonb;
    v_process_id uuid;
    v_flow_name text;
    v_flow_version integer;
    v_payload_hash text;
BEGIN
    BEGIN
        v_op.operation_id := v_op_id::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op_id::uuid;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    IF v_op.process_id IS NOT NULL THEN
        SELECT flow_name, flow_version INTO v_flow_name, v_flow_version
        FROM workflow.process_instance WHERE process_id = v_op.process_id;
        RETURN jsonb_build_object(
            'status', 'ok', 'outcome', 'PROCESSING',
            'result', jsonb_build_object(
                'operationId', v_op.operation_id::text,
                'processId', v_op.process_id::text,
                'flowName', v_flow_name,
                'flowVersion', v_flow_version,
                'status', 'PROCESSING'),
            'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
    END IF;

    IF v_op.status <> 'CREATED' THEN
        RETURN payment._domain_error('payload.invalid', 'operation is not in CREATED state', v_correlation);
    END IF;

    SELECT * INTO v_binding FROM payment.flow_binding WHERE operation_kind = v_op.operation_kind;
    IF NOT FOUND THEN
        RETURN payment._domain_error('payload.invalid', 'no flow binding for operation kind', v_correlation);
    END IF;

    v_payload_hash := encode(api.digest(p_payload::text, 'sha256'), 'hex');

    v_start := workflow.start_process(
        v_binding.flow_name,
        v_op.operation_id::text,
        jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'operationKind', v_op.operation_kind,
            'amount', v_op.amount::text,
            'currency', v_op.currency));

    v_process_id := (v_start ->> 'processId')::uuid;
    v_flow_name := v_start ->> 'flowName';
    v_flow_version := (v_start ->> 'flowVersion')::integer;

    PERFORM payment._set_status(v_op.operation_id, 'PROCESSING', 'OPERATION_SUBMITTED', v_payload_hash, v_process_id);

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'PROCESSING',
        'result', jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'processId', v_process_id::text,
            'flowName', v_flow_name,
            'flowVersion', v_flow_version,
            'status', 'PROCESSING'),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.validate_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;
    IF v_op.status NOT IN ('PROCESSING', 'COMPLETED', 'REJECTED') THEN
        RETURN payment._domain_error('payload.invalid', 'operation is not processable', v_correlation);
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'VALID',
        'result', jsonb_build_object('operationId', v_op.operation_id::text, 'status', v_op.status),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.prepare_external_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery, api
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_external_id text;
    v_payload_hash text;
    v_count integer;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;
    IF v_op.process_id IS NULL THEN
        RETURN payment._domain_error('payload.invalid', 'process is not pinned', v_correlation);
    END IF;

    SELECT count(*) INTO v_count FROM delivery.external_request WHERE operation_id = v_op.operation_id;
    IF v_count = 0 THEN
        v_external_id := gen_random_uuid()::text;
        v_payload_hash := encode(api.digest(
            jsonb_build_object('operationId', v_external_id, 'amount', v_op.amount::text, 'currency', v_op.currency)::text,
            'sha256'), 'hex');
        INSERT INTO delivery.external_request(external_request_id, operation_id, correlation_id, amount, currency, state, payload_hash)
        VALUES (v_external_id, v_op.operation_id, (p_context ->> 'correlationId')::uuid, v_op.amount, v_op.currency, 'CREATED', v_payload_hash);
        INSERT INTO delivery.outbox(external_request_id, correlation_id, state, next_attempt_at)
        VALUES (v_external_id, (p_context ->> 'correlationId')::uuid, 'PENDING', clock_timestamp());
    ELSE
        SELECT external_request_id INTO v_external_id FROM delivery.external_request WHERE operation_id = v_op.operation_id;
        INSERT INTO delivery.outbox(external_request_id, correlation_id, state, next_attempt_at)
        SELECT v_external_id, (p_context ->> 'correlationId')::uuid, 'PENDING', clock_timestamp()
        WHERE NOT EXISTS (SELECT 1 FROM delivery.outbox WHERE external_request_id = v_external_id);
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'PREPARED',
        'result', jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'externalRequestId', v_external_id,
            'state', 'CREATED'),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.apply_receipt_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_external text;
    v_receipt delivery.receipt%ROWTYPE;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    SELECT external_request_id INTO v_external FROM delivery.external_request WHERE operation_id = v_op.operation_id;
    IF v_external IS NULL THEN
        RETURN payment._domain_error('receipt.not_found', 'no receipt is recorded', v_correlation);
    END IF;

    SELECT * INTO v_receipt FROM delivery.receipt WHERE external_request_id = v_external ORDER BY received_at LIMIT 1;
    IF NOT FOUND THEN
        RETURN payment._domain_error('receipt.not_found', 'no receipt is recorded', v_correlation);
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', v_receipt.outcome,
        'result', jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'externalRequestId', v_external,
            'outcome', v_receipt.outcome),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.check_limit_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, workflow
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_step_id uuid;
    v_decision text;
    v_outcome text;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    IF v_op.amount <= 100000.00 THEN
        v_decision := 'WITHIN_LIMIT';
        v_outcome := 'APPROVED';
    ELSE
        v_decision := 'REVIEW_REQUIRED';
        v_outcome := NULL;
    END IF;

    IF v_outcome IS NOT NULL THEN
        SELECT si.step_instance_id INTO v_step_id
        FROM workflow.step_instance si
        JOIN workflow.process_instance p ON p.process_id = si.process_id
        WHERE si.process_id = v_op.process_id
          AND si.state IN ('READY', 'RUNNING')
          AND si.step_key = p.current_step_key
        ORDER BY si.entered_at DESC
        LIMIT 1;
        INSERT INTO payment.decisions(decision_id, process_id, step_instance_id, source, principal, outcome, rule_version)
        VALUES (gen_random_uuid(), v_op.process_id, v_step_id, 'LIMIT_RULE', NULL, v_outcome, 'course-limit-v1')
        ON CONFLICT (step_instance_id) DO NOTHING;
    END IF;

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', v_decision,
        'result', jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'decision', v_decision,
            'ruleVersion', 'course-limit-v1'),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.approve_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery, workflow
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_decision payment.decisions%ROWTYPE;
    v_payload_hash text;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    SELECT * INTO v_decision FROM payment.decisions
    WHERE process_id = v_op.process_id AND outcome = 'APPROVED'
    ORDER BY created_at LIMIT 1;
    IF NOT FOUND THEN
        RETURN payment._domain_error('payment.decision_missing', 'no approved decision', v_correlation);
    END IF;

    v_payload_hash := encode(api.digest(p_payload::text, 'sha256'), 'hex');
    PERFORM payment._set_status(v_op.operation_id, 'COMPLETED', 'OPERATION_COMPLETED', v_payload_hash);

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'APPROVED',
        'result', jsonb_build_object('operationId', v_op.operation_id::text),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.complete_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_external text;
    v_receipt delivery.receipt%ROWTYPE;
    v_payload_hash text;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    IF v_op.operation_kind = 'PAYMENT_EXECUTION' THEN
        SELECT external_request_id INTO v_external FROM delivery.external_request WHERE operation_id = v_op.operation_id;
        SELECT * INTO v_receipt FROM delivery.receipt
        WHERE external_request_id = v_external AND outcome = 'COMPLETED'
        ORDER BY received_at LIMIT 1;
        IF NOT FOUND OR v_receipt.applied_at IS NULL THEN
            RETURN payment._domain_error('payment.evidence_missing', 'no applied completed receipt', v_correlation);
        END IF;
    ELSE
        IF NOT EXISTS (SELECT 1 FROM payment.decisions WHERE process_id = v_op.process_id AND outcome = 'APPROVED') THEN
            RETURN payment._domain_error('payment.evidence_missing', 'no approved decision', v_correlation);
        END IF;
    END IF;

    v_payload_hash := encode(api.digest(p_payload::text, 'sha256'), 'hex');
    PERFORM payment._set_status(v_op.operation_id, 'COMPLETED', 'OPERATION_COMPLETED', v_payload_hash);

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'COMPLETED',
        'result', jsonb_build_object('operationId', v_op.operation_id::text),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.reject_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_external text;
    v_receipt delivery.receipt%ROWTYPE;
    v_payload_hash text;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    IF v_op.operation_kind = 'PAYMENT_EXECUTION' THEN
        SELECT external_request_id INTO v_external FROM delivery.external_request WHERE operation_id = v_op.operation_id;
        SELECT * INTO v_receipt FROM delivery.receipt
        WHERE external_request_id = v_external AND outcome = 'REJECTED'
        ORDER BY received_at LIMIT 1;
        IF NOT FOUND OR v_receipt.applied_at IS NULL THEN
            RETURN payment._domain_error('payment.evidence_missing', 'no applied rejected receipt', v_correlation);
        END IF;
    ELSE
        IF NOT EXISTS (SELECT 1 FROM payment.decisions WHERE process_id = v_op.process_id AND outcome = 'REJECTED') THEN
            RETURN payment._domain_error('payment.evidence_missing', 'no rejected decision', v_correlation);
        END IF;
    END IF;

    v_payload_hash := encode(api.digest(p_payload::text, 'sha256'), 'hex');
    PERFORM payment._set_status(v_op.operation_id, 'REJECTED', 'OPERATION_REJECTED', v_payload_hash);

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'REJECTED',
        'result', jsonb_build_object('operationId', v_op.operation_id::text),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION payment.receipt_accept_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, delivery
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_verified boolean := (p_context #>> '{transport, signatureVerified}')::boolean;
    v_sig_version integer := (p_context #>> '{transport, signatureVersion}')::integer;
    v_body_hash text := p_context #>> '{transport, bodySha256}';
    v_message_id text := p_payload ->> 'messageId';
    v_external_id text := p_payload ->> 'externalRequestId';
    v_outcome text := p_payload ->> 'outcome';
    v_provider_id text := p_payload ->> 'providerPaymentId';
    v_version integer := (p_payload ->> 'version')::integer;
    v_external delivery.external_request%ROWTYPE;
    v_existing delivery.inbox%ROWTYPE;
    v_proc_id uuid;
    v_receipt jsonb;
BEGIN
    IF v_verified IS NOT TRUE THEN
        RETURN payment._domain_error('receipt.signature_required', 'signature is required', v_correlation);
    END IF;
    IF v_sig_version IS DISTINCT FROM 1 THEN
        RETURN payment._domain_error('signature.invalid', 'unsupported signature version', v_correlation);
    END IF;
    IF v_version IS DISTINCT FROM 1 THEN
        RETURN payment._domain_error('payload.invalid', 'unsupported receipt version', v_correlation);
    END IF;
    IF v_body_hash IS NULL OR v_body_hash !~ '^[0-9a-f]{64}$' THEN
        RETURN payment._domain_error('signature.invalid', 'body hash is missing', v_correlation);
    END IF;

    SELECT * INTO v_external FROM delivery.external_request WHERE external_request_id = v_external_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('receipt.external_request_not_found', 'unknown external request', v_correlation);
    END IF;

    SELECT * INTO v_existing FROM delivery.inbox WHERE message_id = v_message_id;
    IF FOUND THEN
        IF v_existing.body_hash = v_body_hash THEN
            RETURN jsonb_build_object(
                'status', 'ok', 'outcome', 'DUPLICATE',
                'result', jsonb_build_object(
                    'messageId', v_message_id,
                    'externalRequestId', v_external_id,
                    'state', v_existing.state),
                'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
        END IF;
        RETURN payment._domain_error('idempotency.conflict', 'messageId is already used with a different body', v_correlation);
    END IF;

    SELECT process_id INTO v_proc_id FROM payment.operations WHERE operation_id = v_external.operation_id;

    v_receipt := jsonb_build_object(
        'externalRequestId', v_external_id,
        'messageId', v_message_id,
        'occurredAt', p_payload ->> 'occurredAt',
        'outcome', v_outcome,
        'providerPaymentId', v_provider_id,
        'version', 1);

    INSERT INTO delivery.inbox(message_id, external_request_id, process_id, signal_type, body_hash, payload, state)
    VALUES (v_message_id, v_external_id, v_proc_id, 'payment.receipt', v_body_hash, v_receipt, 'RECEIVED');

    INSERT INTO delivery.receipt(message_id, external_request_id, message_version, outcome, signature_valid, body_hash, body)
    VALUES (v_message_id, v_external_id, 1, v_outcome, true, v_body_hash, v_receipt);

    UPDATE delivery.external_request SET state = 'CONFIRMED' WHERE external_request_id = v_external_id;
    UPDATE delivery.outbox
    SET state = 'CONFIRMED', delivered_at = COALESCE(delivered_at, clock_timestamp()),
        lease_owner = NULL, lease_until = NULL
    WHERE external_request_id = v_external_id AND state <> 'CONFIRMED';

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'RECEIVED',
        'result', jsonb_build_object(
            'messageId', v_message_id,
            'externalRequestId', v_external_id,
            'state', 'RECEIVED'),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_existing FROM delivery.inbox WHERE message_id = v_message_id;
        IF FOUND AND v_existing.body_hash = v_body_hash THEN
            RETURN jsonb_build_object(
                'status', 'ok', 'outcome', 'DUPLICATE',
                'result', jsonb_build_object(
                    'messageId', v_message_id,
                    'externalRequestId', v_external_id,
                    'state', v_existing.state),
                'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
        END IF;
        RETURN payment._domain_error('idempotency.conflict', 'messageId is already used', v_correlation);
END;
$$;

CREATE OR REPLACE FUNCTION payment.operation_events_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op payment.operations%ROWTYPE;
    v_events jsonb;
BEGIN
    BEGIN
        v_op.operation_id := (p_payload ->> 'operationId')::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN payment._domain_error('payload.invalid', 'invalid operationId', v_correlation);
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op.operation_id;
    IF NOT FOUND THEN
        RETURN payment._domain_error('operation.not_found', 'operation not found', v_correlation);
    END IF;

    SELECT COALESCE(jsonb_agg(jsonb_build_object(
        'eventType', event_type,
        'payloadHash', payload_hash,
        'occurredAt', occurred_at
    ) ORDER BY occurred_at), '[]'::jsonb)
    INTO v_events
    FROM payment.operation_events
    WHERE operation_id = v_op.operation_id;

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', 'FOUND',
        'result', jsonb_build_object('operationId', v_op.operation_id::text, 'events', v_events),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION workflow.manual_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, payment
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_principal text := p_context ->> 'principal';
    v_process_id uuid := (p_payload ->> 'processId')::uuid;
    v_step_id uuid := (p_payload ->> 'stepInstanceId')::uuid;
    v_decision text := p_payload ->> 'decision';
    v_reason text := p_payload ->> 'reason';
    v_proc workflow.process_instance%ROWTYPE;
    v_step workflow.step_instance%ROWTYPE;
    v_reason_hash text;
    v_decision_id uuid;
BEGIN
    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = v_process_id FOR UPDATE;
    IF NOT FOUND THEN
        RETURN payment._domain_error('workflow.process_not_found', 'process not found', v_correlation);
    END IF;

    SELECT * INTO v_step FROM workflow.step_instance WHERE step_instance_id = v_step_id FOR UPDATE;
    IF NOT FOUND OR v_step.process_id <> v_process_id OR v_step.step_type <> 'MANUAL' OR v_step.state <> 'WAITING' THEN
        RETURN payment._domain_error('workflow.decision_conflict', 'step is not awaiting a decision', v_correlation);
    END IF;

    v_reason_hash := encode(api.digest(convert_to(v_reason, 'UTF8'), 'sha256'), 'hex');

    INSERT INTO payment.decisions(decision_id, process_id, step_instance_id, source, principal, reason_hash, outcome)
    VALUES (gen_random_uuid(), v_process_id, v_step_id, 'MANUAL', v_principal, v_reason_hash, v_decision)
    RETURNING decision_id INTO v_decision_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = v_decision, completed_at = clock_timestamp()
    WHERE step_instance_id = v_step_id;

    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (v_process_id, v_step_id, 'ManualDecision');
    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (v_process_id, v_step_id, 'StepCompleted');

    PERFORM workflow._advance(v_process_id, v_decision);

    RETURN jsonb_build_object(
        'status', 'ok', 'outcome', v_decision,
        'result', jsonb_build_object(
            'decisionId', v_decision_id::text,
            'processId', v_process_id::text,
            'stepInstanceId', v_step_id::text,
            'decision', v_decision,
            'source', 'MANUAL',
            'principal', v_principal),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1));
EXCEPTION
    WHEN unique_violation THEN
        RETURN payment._domain_error('workflow.decision_conflict', 'decision is already recorded', v_correlation);
END;
$$;

-- Ownership and grants for week-3 functions.
ALTER FUNCTION delivery.claim_outbox(text,integer) OWNER TO course_owner;
ALTER FUNCTION delivery.succeed_outbox(uuid,text,bigint,text) OWNER TO course_owner;
ALTER FUNCTION delivery.fail_outbox(uuid,text,bigint,text) OWNER TO course_owner;
ALTER FUNCTION delivery.apply_inbox_for_process(uuid,text) OWNER TO course_owner;
ALTER FUNCTION delivery.reconcile_inbox(integer) OWNER TO course_owner;
ALTER FUNCTION payment._domain_error(text,text,text) OWNER TO course_target;
ALTER FUNCTION payment._set_status(uuid,text,text,text,uuid) OWNER TO course_owner;
ALTER FUNCTION payment.submit_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.validate_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.prepare_external_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.apply_receipt_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.check_limit_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.approve_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.complete_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.reject_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.receipt_accept_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.operation_events_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION workflow.manual_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION workflow._enter_step(uuid,text) OWNER TO course_owner;

REVOKE ALL ON ALL FUNCTIONS IN SCHEMA delivery FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA payment FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA workflow FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA training FROM PUBLIC;

GRANT EXECUTE ON FUNCTION delivery.claim_outbox(text,integer) TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.succeed_outbox(uuid,text,bigint,text) TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.fail_outbox(uuid,text,bigint,text) TO course_owner, outbox_dispatcher;
GRANT EXECUTE ON FUNCTION delivery.reconcile_inbox(integer) TO course_owner, inbox_reconciler;
GRANT EXECUTE ON FUNCTION delivery.apply_inbox_for_process(uuid,text) TO course_owner;

GRANT EXECUTE ON FUNCTION payment._domain_error(text,text,text) TO course_owner, course_target;
GRANT EXECUTE ON FUNCTION payment._set_status(uuid,text,text,text,uuid) TO course_owner, course_target;
GRANT EXECUTE ON FUNCTION payment.submit_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.validate_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.prepare_external_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.apply_receipt_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.check_limit_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.approve_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.complete_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.reject_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.receipt_accept_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.operation_events_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION workflow.manual_v1(jsonb,jsonb) TO course_owner;

-- course_target needs workflow internals to submit, decide and advance.
GRANT EXECUTE ON FUNCTION workflow.start_process(text,text,jsonb) TO course_target;
GRANT EXECUTE ON FUNCTION workflow._advance(uuid,text) TO course_target;
GRANT SELECT, UPDATE ON workflow.process_instance TO course_target;
GRANT SELECT, UPDATE ON workflow.step_instance TO course_target;
GRANT SELECT, INSERT ON workflow.workflow_event TO course_target;
GRANT SELECT, UPDATE ON workflow.workflow_signal TO course_target;
GRANT USAGE ON SCHEMA workflow TO course_target;

-- Rediscover after blanket revokes: ensure explicit grants are preserved.
GRANT EXECUTE ON FUNCTION payment.request_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.get_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION training.canary_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION training.canary_v2(jsonb,jsonb) TO course_owner;

-- Domain tables created by week-3 migrations: ownership and grants.
-- course_target reads payment.operations (already granted in 005); status
-- transitions go through payment._set_status owned by course_owner so that
-- course_migration (member of course_target) never gets direct DML.
ALTER TABLE payment.flow_binding OWNER TO course_owner;
ALTER TABLE delivery.outbox_policy OWNER TO course_owner;
GRANT SELECT, INSERT ON payment.flow_binding TO course_target;
GRANT SELECT ON delivery.outbox_policy TO course_target;
GRANT SELECT ON payment.operation_events TO course_target;

-- Catalog: payment.submit.
INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES ('payment','submit',1,'POST','payment','submit_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","processId","flowName","flowVersion","status"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "processId":{"type":"string","format":"uuid"},
      "flowName":{"enum":["payment-processing","payment-review"]},
      "flowVersion":{"type":"integer","minimum":1},
      "status":{"const":"PROCESSING"}
    }
  }'::jsonb,
 '["PROCESSING"]'::jsonb,
 '["payment:write"]'::jsonb,
 'required','principal_action',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

-- Catalog: operation.events.
INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES ('operation','events',1,'POST','payment','operation_events_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","events"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "events":{"type":"array","items":{"type":"object"}}
    }
  }'::jsonb,
 '["FOUND"]'::jsonb,
 '["payment:read"]'::jsonb,
 'none','none',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

-- Catalog: internal payment actions (worker-invoked via api.invoke).
INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES
('payment','validate',1,'POST','payment','validate_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","status"],
    "properties":{"operationId":{"type":"string","format":"uuid"},"status":{"type":"string"}}
  }'::jsonb,
 '["VALID"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','prepare_external',1,'POST','payment','prepare_external_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","externalRequestId","state"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "externalRequestId":{"type":"string","minLength":1,"maxLength":128},
      "state":{"const":"CREATED"}
    }
  }'::jsonb,
 '["PREPARED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','apply_receipt',1,'POST','payment','apply_receipt_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","externalRequestId","outcome"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "externalRequestId":{"type":"string","minLength":1,"maxLength":128},
      "outcome":{"enum":["COMPLETED","REJECTED"]}
    }
  }'::jsonb,
 '["COMPLETED","REJECTED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','complete',1,'POST','payment','complete_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '["COMPLETED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','reject',1,'POST','payment','reject_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '["REJECTED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','check_limit',1,'POST','payment','check_limit_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","decision","ruleVersion"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "decision":{"enum":["WITHIN_LIMIT","REVIEW_REQUIRED"]},
      "ruleVersion":{"const":"course-limit-v1"}
    }
  }'::jsonb,
 '["WITHIN_LIMIT","REVIEW_REQUIRED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1'),
('payment','approve',1,'POST','payment','approve_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '["APPROVED"]'::jsonb,
 '["payment:internal"]'::jsonb,
 'none','none',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

-- Catalog: receipt.accept.
INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES ('receipt','accept',1,'POST','payment','receipt_accept_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["externalRequestId","messageId","occurredAt","outcome","providerPaymentId","version"],
    "properties":{
      "externalRequestId":{"type":"string","minLength":1,"maxLength":128},
      "messageId":{"type":"string","minLength":1,"maxLength":128},
      "occurredAt":{"type":"string","maxLength":64},
      "outcome":{"enum":["COMPLETED","REJECTED"]},
      "providerPaymentId":{"type":"string","minLength":1,"maxLength":128},
      "version":{"const":1}
    }
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["messageId","externalRequestId","state"],
    "properties":{
      "messageId":{"type":"string","minLength":1,"maxLength":128},
      "externalRequestId":{"type":"string","minLength":1,"maxLength":128},
      "state":{"enum":["RECEIVED","APPLIED"]}
    }
  }'::jsonb,
 '["RECEIVED","DUPLICATE"]'::jsonb,
 '["receipt:write"]'::jsonb,
 'required','principal_action',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

-- Catalog: workflow.manual.
INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES ('workflow','manual',1,'POST','workflow','manual_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["processId","stepInstanceId","decision","reason"],
    "properties":{
      "processId":{"type":"string","format":"uuid"},
      "stepInstanceId":{"type":"string","format":"uuid"},
      "decision":{"enum":["APPROVED","REJECTED"]},
      "reason":{"type":"string","minLength":1,"maxLength":500}
    }
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["decisionId","processId","stepInstanceId","decision","source","principal"],
    "properties":{
      "decisionId":{"type":"string","format":"uuid"},
      "processId":{"type":"string","format":"uuid"},
      "stepInstanceId":{"type":"string","format":"uuid"},
      "decision":{"enum":["APPROVED","REJECTED"]},
      "source":{"const":"MANUAL"},
      "principal":{"type":"string","minLength":1,"maxLength":128}
    }
  }'::jsonb,
 '["APPROVED","REJECTED"]'::jsonb,
 '["workflow:manual"]'::jsonb,
 'required','principal_action',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;