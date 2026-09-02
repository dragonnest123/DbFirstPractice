CREATE OR REPLACE FUNCTION api.invoke(
    p_module text,
    p_action text,
    p_version integer,
    p_context jsonb,
    p_payload jsonb
) RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, pg_temp
AS $$
DECLARE
    v_catalog api.action_catalog%ROWTYPE;
    v_effective_version int;
    v_principal text := p_context ->> 'principal';
    v_correlation text := p_context ->> 'correlationId';
    v_request_id text := p_context ->> 'requestId';
    v_scopes jsonb := COALESCE(p_context -> 'scopes', '[]'::jsonb);
    v_required jsonb;
    v_needed text;
    v_result jsonb;
    v_envelope jsonb;
    v_target_schema text;
    v_target_func text;
    v_has_scope boolean;
    v_func_oid oid;
BEGIN
    IF p_version IS NOT NULL THEN
        SELECT * INTO v_catalog
        FROM api.action_catalog
        WHERE module = p_module AND action = p_action AND version = p_version;
        IF NOT FOUND OR NOT v_catalog.enabled THEN
            RETURN jsonb_build_object(
                'status','error','code','action.not_found','message','unknown or disabled action',
                'retryable', false, 'details','{}'::jsonb,
                'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', p_version)
            );
        END IF;
    ELSE
        SELECT * INTO v_catalog
        FROM api.action_catalog
        WHERE module = p_module AND action = p_action AND is_default;
        IF NOT FOUND THEN
            RETURN jsonb_build_object(
                'status','error','code','action.not_found','message','no default version',
                'retryable', false, 'details','{}'::jsonb,
                'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', NULL)
            );
        END IF;
    END IF;

    v_effective_version := v_catalog.version;
    v_target_schema := v_catalog.target_schema;
    v_target_func := v_catalog.target_function;

    BEGIN
        v_required := COALESCE(v_catalog.required_policy, '[]'::jsonb);
        FOR v_needed IN SELECT jsonb_array_elements_text(v_required)
        LOOP
            SELECT EXISTS(SELECT 1 FROM jsonb_array_elements_text(v_scopes) s WHERE s = v_needed) INTO v_has_scope;
            IF NOT v_has_scope THEN
                RETURN jsonb_build_object(
                    'status','error','code','access.denied','message','insufficient policy',
                    'retryable', false, 'details','{}'::jsonb,
                    'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', v_effective_version)
                );
            END IF;
        END LOOP;
    EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object(
            'status','error','code','internal.error','message','invalid policy context',
            'retryable', false, 'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', v_effective_version)
        );
    END;

    SELECT p.oid INTO v_func_oid
    FROM pg_proc p
    JOIN pg_namespace n ON n.oid = p.pronamespace
    WHERE n.nspname = v_target_schema
      AND p.proname = v_target_func
      AND p.pronargs = 2
      AND p.proargtypes[0] = 'jsonb'::regtype::oid
      AND p.proargtypes[1] = 'jsonb'::regtype::oid
      AND p.prorettype = 'jsonb'::regtype::oid;
    IF NOT FOUND THEN
        RETURN jsonb_build_object(
            'status','error','code','internal.error','message','invalid target signature',
            'retryable', false, 'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', v_effective_version)
        );
    END IF;

    BEGIN
        EXECUTE format('SELECT %I.%I($1,$2)', v_target_schema, v_target_func)
        USING p_context, p_payload INTO v_result;
    EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object(
            'status','error','code','internal.error','message','target failed',
            'retryable', false, 'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', v_effective_version)
        );
    END;

    IF v_result IS NULL OR NOT (v_result ? 'status') THEN
        RETURN jsonb_build_object(
            'status','error','code','internal.error','message','invalid target envelope',
            'retryable', false, 'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', v_effective_version)
        );
    END IF;

    IF COALESCE((p_context ->> 'recordDispatch')::boolean, false) THEN
        IF v_correlation IS NULL
           OR v_correlation !~ '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'
           OR v_request_id IS NULL OR v_request_id = '' THEN
            RAISE EXCEPTION 'internal.error: invalid record context';
        END IF;
        IF v_result ->> 'status' = 'ok' THEN
            INSERT INTO api.action_dispatches(
                correlation_id, request_id, module, action, version, principal, payload_hash, status, outcome)
            VALUES (
                v_correlation::uuid, v_request_id, p_module, p_action, v_effective_version,
                v_principal, encode(api.digest(p_payload::text, 'sha256'), 'hex'), 'OK', v_result ->> 'outcome');
        END IF;
    END IF;

    RETURN v_result;
END;
$$;

ALTER FUNCTION api.invoke(text,text,integer,jsonb,jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO course_migration;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO workflow_worker;

INSERT INTO api.action_catalog(
    module, action, version, http_method, target_schema, target_function,
    request_schema, response_schema, outcomes, required_policy,
    idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES (
    'workflow','get',1,'POST','workflow','get_v1',
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":false,
        "required":["processId"],
        "properties":{"processId":{"type":"string","format":"uuid"}}
    }'::jsonb,
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":true
    }'::jsonb,
    '["FOUND"]'::jsonb,
    '["workflow:read"]'::jsonb,
    'none','none',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

CREATE OR REPLACE VIEW autocheck.flow_versions AS
SELECT flow_name::text, version::int AS flow_version, status::text, is_active::boolean, published_at::timestamptz
FROM workflow.flow_version;

CREATE OR REPLACE VIEW autocheck.processes AS
SELECT process_id::uuid, business_key::text, flow_name::text, flow_version::int,
       state::text, current_step_key::text, created_at::timestamptz, updated_at::timestamptz
FROM workflow.process_instance;

CREATE OR REPLACE VIEW autocheck.steps AS
SELECT step_instance_id::uuid, process_id::uuid, step_key::text, step_type::text,
       state::text, outcome::text, entered_at::timestamptz, completed_at::timestamptz
FROM workflow.step_instance;

CREATE OR REPLACE VIEW autocheck.jobs AS
SELECT job_id::uuid, process_id::uuid, step_instance_id::uuid, execution_id::uuid,
       state::text, lease_owner::text, lease_version::bigint, lease_until::timestamptz,
       attempt_count::int, next_attempt_at::timestamptz
FROM workflow.workflow_job;

CREATE OR REPLACE VIEW autocheck.attempts AS
SELECT attempt_id::uuid, job_id::uuid, execution_id::uuid, lease_version::bigint,
       attempt_number::int, status::text, outcome::text, error_code::text,
       started_at::timestamptz, finished_at::timestamptz
FROM workflow.task_attempt;

CREATE OR REPLACE VIEW autocheck.signals AS
SELECT message_id::text, process_id::uuid, signal_type::text, body_hash::text,
       status::text, received_at::timestamptz
FROM workflow.workflow_signal;

CREATE OR REPLACE VIEW autocheck.workflow_events AS
SELECT event_id::uuid, process_id::uuid, step_instance_id::uuid, event_type::text, occurred_at::timestamptz
FROM workflow.workflow_event;

ALTER VIEW autocheck.flow_versions OWNER TO course_owner;
ALTER VIEW autocheck.processes OWNER TO course_owner;
ALTER VIEW autocheck.steps OWNER TO course_owner;
ALTER VIEW autocheck.jobs OWNER TO course_owner;
ALTER VIEW autocheck.attempts OWNER TO course_owner;
ALTER VIEW autocheck.signals OWNER TO course_owner;
ALTER VIEW autocheck.workflow_events OWNER TO course_owner;

GRANT USAGE ON SCHEMA workflow TO workflow_worker, course_publication, course_runtime;
GRANT USAGE ON SCHEMA api TO workflow_worker;
GRANT USAGE ON SCHEMA autocheck TO course_publication;
GRANT SELECT ON autocheck.flow_versions TO course_publication;
GRANT SELECT ON autocheck.flow_versions, autocheck.processes, autocheck.steps,
    autocheck.jobs, autocheck.attempts, autocheck.signals, autocheck.workflow_events
    TO course_runtime;