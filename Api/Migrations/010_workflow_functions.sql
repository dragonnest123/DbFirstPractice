CREATE OR REPLACE FUNCTION workflow._advance(p_process_id uuid, p_outcome text)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_proc workflow.process_instance%ROWTYPE;
    v_next text;
BEGIN
    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = p_process_id FOR UPDATE;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.process_not_found: process % does not exist', p_process_id;
    END IF;

    IF v_proc.state IN ('COMPLETED','FAILED') THEN
        RETURN;
    END IF;

    SELECT t.to_step_key INTO v_next
    FROM workflow.transition_definition t
    WHERE t.flow_name = v_proc.flow_name
      AND t.flow_version = v_proc.flow_version
      AND t.from_step_key = v_proc.current_step_key
      AND t.outcome = p_outcome;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.unknown_outcome: no transition for outcome % from step %',
            p_outcome, v_proc.current_step_key;
    END IF;

    PERFORM workflow._enter_step(p_process_id, v_next);
END;
$$;

CREATE OR REPLACE FUNCTION workflow._apply_ready_signals(
    p_process_id uuid, p_step_instance_id uuid, p_params jsonb)
RETURNS void
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_signal_type text := p_params ->> 'signal_type';
    v_outcome text := p_params ->> 'outcome';
    v_signal workflow.workflow_signal%ROWTYPE;
BEGIN
    SELECT * INTO v_signal
    FROM workflow.workflow_signal
    WHERE process_id = p_process_id
      AND signal_type = v_signal_type
      AND status = 'ACCEPTED'
    ORDER BY received_at, message_id
    LIMIT 1
    FOR UPDATE;
    IF NOT FOUND THEN
        RETURN;
    END IF;

    UPDATE workflow.workflow_signal SET status = 'APPLIED' WHERE message_id = v_signal.message_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = v_outcome, completed_at = clock_timestamp()
    WHERE step_instance_id = p_step_instance_id;

    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (p_process_id, p_step_instance_id, 'SignalApplied');

    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (p_process_id, p_step_instance_id, 'StepCompleted');

    PERFORM workflow._advance(p_process_id, v_outcome);
END;
$$;

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

CREATE OR REPLACE FUNCTION workflow.publish_flow(p_map jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_flow text := p_map ->> 'flow_name';
    v_version integer := (p_map ->> 'version')::integer;
    v_existing workflow.flow_version%ROWTYPE;
    v_step jsonb;
    v_transition jsonb;
    v_step_key text;
    v_step_type text;
    v_idx integer := 0;
    v_task jsonb;
BEGIN
    IF NOT (p_map ? 'contract_version' AND p_map ? 'flow_name' AND p_map ? 'version'
            AND p_map ? 'start_step' AND p_map ? 'steps' AND p_map ? 'transitions')
       OR p_map ->> 'contract_version' <> 'course-1'
       OR v_flow !~ '^[a-z][a-z0-9_-]{0,62}$'
       OR v_version < 1 THEN
        RAISE EXCEPTION 'manifest.invalid: map does not match contract';
    END IF;

    SELECT * INTO v_existing
    FROM workflow.flow_version
    WHERE flow_name = v_flow AND version = v_version;

    IF FOUND THEN
        IF v_existing.map = p_map THEN
            RETURN jsonb_build_object('status','exists','flowName',v_flow,'flowVersion',v_version);
        END IF;
        RAISE EXCEPTION 'manifest.conflict: published flow version is immutable';
    END IF;

    INSERT INTO workflow.flow_definition(flow_name)
    VALUES (v_flow)
    ON CONFLICT (flow_name) DO NOTHING;

    INSERT INTO workflow.flow_version(flow_name, version, status, is_active, map)
    VALUES (v_flow, v_version, 'PUBLISHED', false, p_map);

    FOR v_step IN SELECT jsonb_array_elements(p_map -> 'steps')
    LOOP
        v_idx := v_idx + 1;
        v_step_key := v_step ->> 'key';
        v_step_type := upper(v_step ->> 'type');
        INSERT INTO workflow.step_definition(flow_name, flow_version, step_key, step_type, params, sort_order)
        VALUES (v_flow, v_version, v_step_key, v_step_type,
                v_step - 'key' - 'type' - 'task', v_idx);

        IF v_step_type = 'AUTOMATIC' THEN
            v_task := v_step -> 'task';
            INSERT INTO workflow.task_definition(
                flow_name, flow_version, step_key, service, module, action, action_version,
                required_policy, timeout_ms, retry, input_mapping, input_constants)
            VALUES (v_flow, v_version, v_step_key,
                    v_task ->> 'service', v_task ->> 'module', v_task ->> 'action',
                    (v_task ->> 'action_version')::integer, v_task -> 'required_policy',
                    (v_task ->> 'timeout_ms')::integer, v_task -> 'retry',
                    v_task -> 'input_mapping',
                    COALESCE(v_task -> 'input_constants', '{}'::jsonb));
        END IF;
    END LOOP;

    FOR v_transition IN SELECT jsonb_array_elements(p_map -> 'transitions')
    LOOP
        INSERT INTO workflow.transition_definition(flow_name, flow_version, from_step_key, outcome, to_step_key)
        VALUES (v_flow, v_version,
                v_transition ->> 'from', v_transition ->> 'outcome', v_transition ->> 'to');
    END LOOP;

    RETURN jsonb_build_object('status','published','flowName',v_flow,'flowVersion',v_version);
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_existing
        FROM workflow.flow_version
        WHERE flow_name = v_flow AND version = v_version;
        IF FOUND THEN
            IF v_existing.map = p_map THEN
                RETURN jsonb_build_object('status','exists','flowName',v_flow,'flowVersion',v_version);
            END IF;
            RAISE EXCEPTION 'manifest.conflict: published flow version is immutable';
        END IF;
        RAISE;
    WHEN others THEN
        IF SQLERRM LIKE 'manifest.%' THEN
            RAISE;
        END IF;
        RAISE EXCEPTION 'manifest.invalid: %', SQLERRM;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.activate_flow(p_flow text, p_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_row workflow.flow_version%ROWTYPE;
BEGIN
    IF p_flow !~ '^[a-z][a-z0-9_-]{0,62}$' OR p_version < 1 THEN
        RAISE EXCEPTION 'flow.invalid: invalid flow route';
    END IF;

    SELECT * INTO v_row FROM workflow.flow_version
    WHERE flow_name = p_flow AND version = p_version;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'flow.not_found: version % of flow % is not published', p_version, p_flow;
    END IF;

    UPDATE workflow.flow_version SET is_active = false
    WHERE flow_name = p_flow AND is_active;
    UPDATE workflow.flow_version SET is_active = true
    WHERE flow_name = p_flow AND version = p_version;

    RETURN jsonb_build_object('status','activated','flowName',p_flow,'flowVersion',p_version);
END;
$$;

CREATE OR REPLACE FUNCTION workflow.start_process(
    p_flow text, p_business_key text, p_data jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_version workflow.flow_version%ROWTYPE;
    v_existing workflow.process_instance%ROWTYPE;
    v_process_id uuid;
    v_start_step text;
    v_state text;
BEGIN
    IF p_flow !~ '^[a-z][a-z0-9_-]{0,62}$' THEN
        RAISE EXCEPTION 'flow.invalid: invalid flow name';
    END IF;
    IF p_business_key IS NULL OR p_business_key = '' THEN
        RAISE EXCEPTION 'process.invalid: business key is required';
    END IF;
    p_data := COALESCE(p_data, '{}'::jsonb);

    SELECT * INTO v_existing
    FROM workflow.process_instance
    WHERE flow_name = p_flow AND business_key = p_business_key;
    IF FOUND THEN
        IF v_existing.data = p_data THEN
            RETURN jsonb_build_object(
                'status','started',
                'processId', v_existing.process_id::text,
                'flowName', v_existing.flow_name,
                'flowVersion', v_existing.flow_version,
                'state', v_existing.state);
        END IF;
        RAISE EXCEPTION 'workflow.start_conflict: business key % is already used with different data',
            p_business_key;
    END IF;

    SELECT * INTO v_version
    FROM workflow.flow_version
    WHERE flow_name = p_flow AND is_active
    ORDER BY version DESC
    LIMIT 1;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'flow.inactive: no active version of flow %', p_flow;
    END IF;

    v_start_step := v_version.map ->> 'start_step';

    INSERT INTO workflow.process_instance(flow_name, flow_version, business_key, state, data)
    VALUES (p_flow, v_version.version, p_business_key, 'CREATED', p_data)
    RETURNING process_id INTO v_process_id;

    INSERT INTO workflow.workflow_event(process_id, event_type)
    VALUES (v_process_id, 'ProcessStarted');

    PERFORM workflow._enter_step(v_process_id, v_start_step);

    SELECT state INTO v_state FROM workflow.process_instance WHERE process_id = v_process_id;

    RETURN jsonb_build_object(
        'status','started',
        'processId', v_process_id::text,
        'flowName', p_flow,
        'flowVersion', v_version.version,
        'state', v_state);
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_existing
        FROM workflow.process_instance
        WHERE flow_name = p_flow AND business_key = p_business_key;
        IF FOUND AND v_existing.data = p_data THEN
            RETURN jsonb_build_object(
                'status','started',
                'processId', v_existing.process_id::text,
                'flowName', v_existing.flow_name,
                'flowVersion', v_existing.flow_version,
                'state', v_existing.state);
        END IF;
        RAISE EXCEPTION 'workflow.start_conflict: business key % is already used with different data',
            p_business_key;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.accept_signal(
    p_process_id uuid, p_signal_type text, p_message_id text, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_proc workflow.process_instance%ROWTYPE;
    v_declared boolean;
    v_body_hash text;
    v_existing workflow.workflow_signal%ROWTYPE;
    v_step_instance workflow.step_instance%ROWTYPE;
    v_step workflow.step_definition%ROWTYPE;
BEGIN
    IF p_signal_type !~ '^[a-z][a-z0-9_-]*(\.[a-z][a-z0-9_-]*)*$' THEN
        RAISE EXCEPTION 'signal.invalid: invalid signal type';
    END IF;
    IF p_message_id IS NULL OR p_message_id = '' THEN
        RAISE EXCEPTION 'signal.invalid: message id is required';
    END IF;
    p_payload := COALESCE(p_payload, '{}'::jsonb);

    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = p_process_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.process_not_found: process % does not exist', p_process_id;
    END IF;

    SELECT EXISTS (
        SELECT 1 FROM workflow.step_definition
        WHERE flow_name = v_proc.flow_name AND flow_version = v_proc.flow_version
          AND step_type = 'WAIT_SIGNAL'
          AND params ->> 'signal_type' = p_signal_type
    ) INTO v_declared;
    IF NOT v_declared THEN
        RAISE EXCEPTION 'signal.unknown: signal type % is not declared in pinned map', p_signal_type;
    END IF;

    v_body_hash := encode(api.digest(p_payload::text, 'sha256'), 'hex');

    SELECT * INTO v_existing FROM workflow.workflow_signal WHERE message_id = p_message_id;
    IF FOUND THEN
        IF v_existing.process_id = p_process_id
           AND v_existing.signal_type = p_signal_type
           AND v_existing.body_hash = v_body_hash THEN
            RETURN jsonb_build_object(
                'status','duplicate',
                'processId', p_process_id::text,
                'messageId', p_message_id,
                'signalType', p_signal_type);
        END IF;
        RAISE EXCEPTION 'signal.conflict: message id % is already used', p_message_id;
    END IF;

    INSERT INTO workflow.workflow_signal(message_id, process_id, signal_type, body_hash, status)
    VALUES (p_message_id, p_process_id, p_signal_type, v_body_hash, 'ACCEPTED');

    INSERT INTO workflow.workflow_event(process_id, event_type)
    VALUES (p_process_id, 'SignalAccepted');

    PERFORM 1 FROM workflow.process_instance WHERE process_id = p_process_id FOR UPDATE;

    SELECT si.* INTO v_step_instance
    FROM workflow.step_instance si
    WHERE si.process_id = p_process_id
      AND si.step_type = 'WAIT_SIGNAL'
      AND si.state = 'WAITING'
    ORDER BY si.entered_at DESC
    LIMIT 1;

    IF FOUND THEN
        SELECT * INTO v_step FROM workflow.step_definition
        WHERE flow_name = v_proc.flow_name AND flow_version = v_proc.flow_version
          AND step_key = v_step_instance.step_key;
        IF v_step.params ->> 'signal_type' = p_signal_type THEN
            PERFORM workflow._apply_ready_signals(
                p_process_id, v_step_instance.step_instance_id, v_step.params);
        END IF;
    END IF;

    RETURN jsonb_build_object(
        'status','accepted',
        'processId', p_process_id::text,
        'messageId', p_message_id,
        'signalType', p_signal_type);
EXCEPTION
    WHEN unique_violation THEN
        SELECT * INTO v_existing FROM workflow.workflow_signal WHERE message_id = p_message_id;
        IF FOUND THEN
            IF v_existing.process_id = p_process_id
               AND v_existing.signal_type = p_signal_type
               AND v_existing.body_hash = v_body_hash THEN
                RETURN jsonb_build_object(
                    'status','duplicate',
                    'processId', p_process_id::text,
                    'messageId', p_message_id,
                    'signalType', p_signal_type);
            END IF;
            RAISE EXCEPTION 'signal.conflict: message id % is already used', p_message_id;
        END IF;
        SELECT * INTO v_existing FROM workflow.workflow_signal
        WHERE process_id = p_process_id
          AND signal_type = p_signal_type
          AND body_hash = v_body_hash
        LIMIT 1;
        IF FOUND THEN
            RETURN jsonb_build_object(
                'status','duplicate',
                'processId', p_process_id::text,
                'messageId', p_message_id,
                'signalType', p_signal_type);
        END IF;
        RAISE EXCEPTION 'signal.conflict: message id % is already used', p_message_id;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.claim_jobs(p_owner text, p_batch integer, p_lease_ms integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, api, pg_temp
AS $$
DECLARE
    v_job workflow.workflow_job%ROWTYPE;
    v_proc workflow.process_instance%ROWTYPE;
    v_step workflow.step_instance%ROWTYPE;
    v_task workflow.task_definition%ROWTYPE;
    v_action api.action_catalog%ROWTYPE;
    v_attempt_id uuid;
    v_lease_version bigint;
    v_lease_until timestamptz;
    v_results jsonb := '[]'::jsonb;
BEGIN
    IF p_owner IS NULL OR p_owner = '' THEN
        RAISE EXCEPTION 'workflow.claim_invalid: owner is required';
    END IF;
    IF p_batch < 1 OR p_batch > 100 THEN
        RAISE EXCEPTION 'workflow.claim_invalid: invalid batch size';
    END IF;
    IF p_lease_ms < 100 OR p_lease_ms > 600000 THEN
        RAISE EXCEPTION 'workflow.claim_invalid: invalid lease duration';
    END IF;

    UPDATE workflow.task_attempt a
    SET status = 'STALE', finished_at = clock_timestamp()
    FROM workflow.workflow_job j
    WHERE a.job_id = j.job_id
      AND a.status = 'RUNNING'
      AND j.state = 'LEASED'
      AND j.lease_until < clock_timestamp();

    FOR v_job IN
        SELECT * FROM workflow.workflow_job
        WHERE (state IN ('READY','RETRY_WAIT') AND next_attempt_at <= clock_timestamp())
           OR (state = 'LEASED' AND lease_until < clock_timestamp())
        ORDER BY created_at, job_id
        FOR UPDATE SKIP LOCKED
        LIMIT p_batch
    LOOP
        v_lease_version := v_job.lease_version + 1;
        v_lease_until := clock_timestamp() + make_interval(secs => p_lease_ms / 1000.0);

        UPDATE workflow.workflow_job
        SET state = 'LEASED',
            lease_owner = p_owner,
            lease_version = v_lease_version,
            lease_until = v_lease_until,
            attempt_count = v_job.attempt_count + 1,
            next_attempt_at = NULL
        WHERE job_id = v_job.job_id;

        INSERT INTO workflow.task_attempt(job_id, execution_id, lease_version, attempt_number, status)
        VALUES (v_job.job_id, v_job.execution_id, v_lease_version, v_job.attempt_count + 1, 'RUNNING')
        RETURNING attempt_id INTO v_attempt_id;

        SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = v_job.process_id;
        SELECT * INTO v_step FROM workflow.step_instance WHERE step_instance_id = v_job.step_instance_id;

        SELECT * INTO v_task FROM workflow.task_definition
        WHERE flow_name = v_proc.flow_name AND flow_version = v_proc.flow_version
          AND step_key = v_step.step_key;
        IF NOT FOUND THEN
            CONTINUE;
        END IF;

        SELECT * INTO v_action FROM api.action_catalog
        WHERE module = v_task.module AND action = v_task.action AND version = v_task.action_version;

        IF NOT FOUND OR NOT v_action.enabled THEN
            UPDATE workflow.task_attempt
            SET status = 'FAILED', error_code = 'workflow.action_disabled', finished_at = clock_timestamp()
            WHERE attempt_id = v_attempt_id;
            UPDATE workflow.workflow_job
            SET state = 'DEAD', lease_owner = NULL, lease_until = NULL
            WHERE job_id = v_job.job_id;
            UPDATE workflow.step_instance
            SET state = 'FAILED', completed_at = clock_timestamp()
            WHERE step_instance_id = v_job.step_instance_id;
            UPDATE workflow.process_instance SET state = 'FAILED' WHERE process_id = v_job.process_id;
            INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
            VALUES (v_job.process_id, v_job.step_instance_id, 'TaskFailed');
            CONTINUE;
        END IF;

        v_results := v_results || jsonb_build_object(
            'jobId', v_job.job_id::text,
            'processId', v_job.process_id::text,
            'stepInstanceId', v_job.step_instance_id::text,
            'executionId', v_job.execution_id::text,
            'leaseVersion', v_lease_version,
            'attemptId', v_attempt_id::text,
            'attemptNumber', v_job.attempt_count + 1,
            'owner', p_owner,
            'flowName', v_proc.flow_name,
            'flowVersion', v_proc.flow_version,
            'stepKey', v_step.step_key,
            'stepType', v_step.step_type,
            'processData', v_proc.data,
            'task', jsonb_build_object(
                'service', v_task.service,
                'module', v_task.module,
                'action', v_task.action,
                'actionVersion', v_task.action_version,
                'requiredPolicy', v_task.required_policy,
                'timeoutMs', v_task.timeout_ms,
                'retry', v_task.retry,
                'inputMapping', v_task.input_mapping,
                'inputConstants', v_task.input_constants),
            'action', jsonb_build_object(
                'outcomes', v_action.outcomes,
                'responseSchema', v_action.response_schema));
    END LOOP;

    RETURN v_results;
END;
$$;

CREATE OR REPLACE FUNCTION workflow.finish_job(
    p_job_id uuid, p_owner text, p_lease_version bigint, p_outcome text, p_result jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_job workflow.workflow_job%ROWTYPE;
    v_step workflow.step_instance%ROWTYPE;
    v_proc workflow.process_instance%ROWTYPE;
    v_state text;
BEGIN
    SELECT * INTO v_job FROM workflow.workflow_job WHERE job_id = p_job_id FOR UPDATE;
    IF NOT FOUND OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version <> p_lease_version THEN
        RAISE EXCEPTION 'workflow.lease_stale: job % is not leased by % with lease version %',
            p_job_id, p_owner, p_lease_version;
    END IF;

    SELECT * INTO v_step FROM workflow.step_instance WHERE step_instance_id = v_job.step_instance_id;
    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = v_job.process_id;

    IF NOT EXISTS (
        SELECT 1 FROM workflow.transition_definition
        WHERE flow_name = v_proc.flow_name AND flow_version = v_proc.flow_version
          AND from_step_key = v_step.step_key AND outcome = p_outcome
    ) THEN
        RAISE EXCEPTION 'workflow.unknown_outcome: no transition for outcome % from step %',
            p_outcome, v_step.step_key;
    END IF;

    UPDATE workflow.task_attempt
    SET status = 'SUCCEEDED', outcome = p_outcome, finished_at = clock_timestamp()
    WHERE job_id = p_job_id AND lease_version = p_lease_version AND status = 'RUNNING';

    UPDATE workflow.workflow_job
    SET state = 'SUCCEEDED', lease_until = NULL
    WHERE job_id = p_job_id;

    UPDATE workflow.step_instance
    SET state = 'COMPLETED', outcome = p_outcome, completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id;

    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (v_job.process_id, v_job.step_instance_id, 'StepCompleted');

    PERFORM workflow._advance(v_job.process_id, p_outcome);

    SELECT state INTO v_state FROM workflow.process_instance WHERE process_id = v_job.process_id;

    RETURN jsonb_build_object(
        'status','finished',
        'processId', v_job.process_id::text,
        'jobId', v_job.job_id::text,
        'state', v_state);
END;
$$;

CREATE OR REPLACE FUNCTION workflow.fail_job(
    p_job_id uuid, p_owner text, p_lease_version bigint, p_error_code text, p_retryable boolean)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_job workflow.workflow_job%ROWTYPE;
    v_step workflow.step_instance%ROWTYPE;
    v_proc workflow.process_instance%ROWTYPE;
    v_task workflow.task_definition%ROWTYPE;
    v_max_attempts integer;
    v_delay_ms integer;
    v_next_attempt_at timestamptz;
BEGIN
    SELECT * INTO v_job FROM workflow.workflow_job WHERE job_id = p_job_id FOR UPDATE;
    IF NOT FOUND OR v_job.state <> 'LEASED'
       OR v_job.lease_owner IS DISTINCT FROM p_owner
       OR v_job.lease_version <> p_lease_version THEN
        RAISE EXCEPTION 'workflow.lease_stale: job % is not leased by % with lease version %',
            p_job_id, p_owner, p_lease_version;
    END IF;

    SELECT * INTO v_step FROM workflow.step_instance WHERE step_instance_id = v_job.step_instance_id;
    SELECT * INTO v_proc FROM workflow.process_instance WHERE process_id = v_job.process_id;

    UPDATE workflow.task_attempt
    SET status = 'FAILED', error_code = p_error_code, finished_at = clock_timestamp()
    WHERE job_id = p_job_id AND lease_version = p_lease_version AND status = 'RUNNING';

    SELECT * INTO v_task FROM workflow.task_definition
    WHERE flow_name = v_proc.flow_name AND flow_version = v_proc.flow_version
      AND step_key = v_step.step_key;
    IF NOT FOUND THEN
        v_max_attempts := 1;
        v_delay_ms := 0;
    ELSE
        v_max_attempts := (v_task.retry ->> 'max_attempts')::integer;
        v_delay_ms := ((v_task.retry -> 'delays_ms') -> (v_job.attempt_count - 1))::integer;
    END IF;

    IF p_retryable AND v_job.attempt_count < v_max_attempts THEN
        v_next_attempt_at := clock_timestamp() + make_interval(secs => v_delay_ms / 1000.0);
        UPDATE workflow.workflow_job
        SET state = 'RETRY_WAIT', lease_owner = NULL, lease_until = NULL,
            next_attempt_at = v_next_attempt_at
        WHERE job_id = p_job_id;
        RETURN jsonb_build_object(
            'status','scheduled',
            'jobId', v_job.job_id::text,
            'nextAttemptAt', to_char(v_next_attempt_at, 'YYYY-MM-DD"T"HH24:MI:SS.USOF'));
    END IF;

    UPDATE workflow.workflow_job
    SET state = 'DEAD', lease_owner = NULL, lease_until = NULL
    WHERE job_id = p_job_id;

    UPDATE workflow.step_instance
    SET state = 'FAILED', completed_at = clock_timestamp()
    WHERE step_instance_id = v_job.step_instance_id;

    UPDATE workflow.process_instance SET state = 'FAILED' WHERE process_id = v_job.process_id;

    INSERT INTO workflow.workflow_event(process_id, step_instance_id, event_type)
    VALUES (v_job.process_id, v_job.step_instance_id, 'TaskFailed');

    RETURN jsonb_build_object(
        'status','dead',
        'jobId', v_job.job_id::text,
        'processId', v_job.process_id::text);
END;
$$;

CREATE OR REPLACE FUNCTION workflow.get_process(p_process_id uuid)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_proc jsonb;
    v_steps jsonb;
    v_jobs jsonb;
    v_attempts jsonb;
    v_row workflow.process_instance%ROWTYPE;
BEGIN
    IF p_process_id IS NULL THEN
        RAISE EXCEPTION 'workflow.process_not_found: process id is required';
    END IF;

    SELECT * INTO v_row FROM workflow.process_instance WHERE process_id = p_process_id;
    IF NOT FOUND THEN
        RAISE EXCEPTION 'workflow.process_not_found: process % does not exist', p_process_id;
    END IF;

    v_proc := jsonb_build_object(
        'processId', v_row.process_id,
        'businessKey', v_row.business_key,
        'flowName', v_row.flow_name,
        'flowVersion', v_row.flow_version,
        'state', v_row.state,
        'currentStepKey', v_row.current_step_key,
        'createdAt', v_row.created_at,
        'updatedAt', v_row.updated_at);

    SELECT COALESCE(jsonb_agg(to_jsonb(s) ORDER BY s.entered_at, s.step_instance_id), '[]'::jsonb)
    INTO v_steps FROM workflow.step_instance s WHERE s.process_id = p_process_id;

    SELECT COALESCE(jsonb_agg(to_jsonb(j) ORDER BY j.created_at, j.job_id), '[]'::jsonb)
    INTO v_jobs FROM workflow.workflow_job j WHERE j.process_id = p_process_id;

    SELECT COALESCE(jsonb_agg(to_jsonb(a) ORDER BY a.attempt_number), '[]'::jsonb)
    INTO v_attempts
    FROM workflow.task_attempt a
    JOIN workflow.workflow_job j ON j.job_id = a.job_id
    WHERE j.process_id = p_process_id;

    RETURN jsonb_build_object(
        'process', v_proc,
        'steps', v_steps,
        'jobs', v_jobs,
        'attempts', v_attempts);
END;
$$;

CREATE OR REPLACE FUNCTION workflow.get_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, workflow, pg_temp
AS $$
DECLARE
    v_process_id text := p_payload ->> 'processId';
    v_data jsonb;
BEGIN
    BEGIN
        v_data := workflow.get_process(v_process_id::uuid);
    EXCEPTION WHEN others THEN
        RETURN jsonb_build_object(
            'status','error',
            'code','workflow.process_not_found',
            'message','process not found',
            'retryable', false,
            'details','{}'::jsonb,
            'meta', jsonb_build_object(
                'correlationId', p_context ->> 'correlationId',
                'actionVersion', 1));
    END;

    RETURN jsonb_build_object(
        'status','ok',
        'outcome','FOUND',
        'result', v_data,
        'meta', jsonb_build_object(
            'correlationId', p_context ->> 'correlationId',
            'actionVersion', 1));
END;
$$;

ALTER FUNCTION workflow._advance(uuid,text) OWNER TO course_owner;
ALTER FUNCTION workflow._apply_ready_signals(uuid,uuid,jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow._enter_step(uuid,text) OWNER TO course_owner;
ALTER FUNCTION workflow.publish_flow(jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.activate_flow(text,integer) OWNER TO course_owner;
ALTER FUNCTION workflow.start_process(text,text,jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.accept_signal(uuid,text,text,jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.claim_jobs(text,integer,integer) OWNER TO course_owner;
ALTER FUNCTION workflow.finish_job(uuid,text,bigint,text,jsonb) OWNER TO course_owner;
ALTER FUNCTION workflow.fail_job(uuid,text,bigint,text,boolean) OWNER TO course_owner;
ALTER FUNCTION workflow.get_process(uuid) OWNER TO course_owner;
ALTER FUNCTION workflow.get_v1(jsonb,jsonb) OWNER TO course_owner;

REVOKE ALL ON FUNCTION workflow._advance(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow._apply_ready_signals(uuid,uuid,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow._enter_step(uuid,text) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.publish_flow(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.activate_flow(text,integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.start_process(text,text,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.accept_signal(uuid,text,text,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.claim_jobs(text,integer,integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.finish_job(uuid,text,bigint,text,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.fail_job(uuid,text,bigint,text,boolean) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.get_process(uuid) FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.get_v1(jsonb,jsonb) FROM PUBLIC;

GRANT EXECUTE ON FUNCTION workflow.claim_jobs(text,integer,integer) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid,text,bigint,text,jsonb) TO workflow_worker;
GRANT EXECUTE ON FUNCTION workflow.fail_job(uuid,text,bigint,text,boolean) TO workflow_worker;

GRANT EXECUTE ON FUNCTION workflow.publish_flow(jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.activate_flow(text,integer) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.start_process(text,text,jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.accept_signal(uuid,text,text,jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.finish_job(uuid,text,bigint,text,jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION workflow.get_process(uuid) TO course_publication;

GRANT EXECUTE ON FUNCTION workflow.publish_flow(jsonb) TO course_migration;
GRANT EXECUTE ON FUNCTION workflow.activate_flow(text,integer) TO course_migration;