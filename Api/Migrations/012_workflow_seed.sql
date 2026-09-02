CREATE SCHEMA IF NOT EXISTS training;

CREATE TABLE IF NOT EXISTS training.canary_effects (
    execution_id text PRIMARY KEY,
    value text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE OR REPLACE FUNCTION training.canary_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, training
AS $$
DECLARE
    v_execution_id text := p_context ->> 'executionId';
    v_value text := p_payload ->> 'value';
    v_stored text;
BEGIN
    IF v_execution_id IS NULL OR v_execution_id = '' THEN
        RETURN jsonb_build_object(
            'status','error','code','idempotency.required','message','missing executionId',
            'retryable',false,'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId', 'actionVersion', 1));
    END IF;

    INSERT INTO training.canary_effects(execution_id, value)
    VALUES (v_execution_id, v_value)
    ON CONFLICT (execution_id) DO NOTHING;

    SELECT value INTO v_stored FROM training.canary_effects WHERE execution_id = v_execution_id;

    RETURN jsonb_build_object(
        'status','ok',
        'outcome','APPLIED',
        'result', jsonb_build_object('stored', true, 'echo', v_stored),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId', 'actionVersion', 1));
END;
$$;

CREATE OR REPLACE FUNCTION training.canary_v2(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, training
AS $$
DECLARE
    v_execution_id text := p_context ->> 'executionId';
    v_value text := p_payload ->> 'value';
    v_stored text;
BEGIN
    IF v_execution_id IS NULL OR v_execution_id = '' THEN
        RETURN jsonb_build_object(
            'status','error','code','idempotency.required','message','missing executionId',
            'retryable',false,'details','{}'::jsonb,
            'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId', 'actionVersion', 2));
    END IF;

    INSERT INTO training.canary_effects(execution_id, value)
    VALUES (v_execution_id, v_value)
    ON CONFLICT (execution_id) DO NOTHING;

    SELECT value INTO v_stored FROM training.canary_effects WHERE execution_id = v_execution_id;

    RETURN jsonb_build_object(
        'status','ok',
        'outcome','APPLIED_V2',
        'result', jsonb_build_object('stored', true, 'echo', v_stored),
        'meta', jsonb_build_object('correlationId', p_context ->> 'correlationId', 'actionVersion', 2));
END;
$$;

ALTER FUNCTION training.canary_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION training.canary_v2(jsonb,jsonb) OWNER TO course_target;
ALTER TABLE training.canary_effects OWNER TO course_owner;
ALTER SCHEMA training OWNER TO course_owner;

GRANT USAGE ON SCHEMA training TO course_runtime;
GRANT SELECT ON training.canary_effects TO course_runtime;

GRANT USAGE, CREATE ON SCHEMA training TO course_target;
GRANT SELECT, INSERT ON training.canary_effects TO course_target;

INSERT INTO api.action_catalog(
    module, action, version, http_method, target_schema, target_function,
    request_schema, response_schema, outcomes, required_policy,
    idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES (
    'training','canary',1,'POST','training','canary_v1',
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":false,
        "required":["value"],
        "properties":{"value":{"type":"string","minLength":1,"maxLength":128}}
    }'::jsonb,
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":false,
        "required":["stored","echo"],
        "properties":{
            "stored":{"type":"boolean"},
            "echo":{"type":"string"}
        }
    }'::jsonb,
    '["APPLIED"]'::jsonb,
    '["workflow:execute"]'::jsonb,
    'required','principal_action',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

INSERT INTO api.action_catalog(
    module, action, version, http_method, target_schema, target_function,
    request_schema, response_schema, outcomes, required_policy,
    idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES (
    'training','canary',2,'POST','training','canary_v2',
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":false,
        "required":["value"],
        "properties":{"value":{"type":"string","minLength":1,"maxLength":128}}
    }'::jsonb,
    '{
        "$schema":"https://json-schema.org/draft/2020-12/schema",
        "type":"object","additionalProperties":false,
        "required":["stored","echo"],
        "properties":{
            "stored":{"type":"boolean"},
            "echo":{"type":"string"}
        }
    }'::jsonb,
    '["APPLIED_V2"]'::jsonb,
    '["workflow:execute"]'::jsonb,
    'required','principal_action',2000,true,false,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

SELECT workflow.publish_flow('{
    "contract_version":"course-1",
    "flow_name":"workflow-smoke",
    "version":1,
    "start_step":"invoke_canary",
    "steps":[
        {
            "key":"invoke_canary",
            "type":"automatic",
            "task":{
                "service":"postgres",
                "module":"training",
                "action":"canary",
                "action_version":1,
                "required_policy":["workflow:execute"],
                "timeout_ms":2000,
                "retry":{"max_attempts":3,"delays_ms":[200,400]},
                "input_mapping":{"/value":"/value"},
                "input_constants":{}
            }
        },
        {"key":"wait_result","type":"wait_signal","signal_type":"training.completed","outcome":"RECEIVED"},
        {"key":"done","type":"end","outcome":"COMPLETED"}
    ],
    "transitions":[
        {"from":"invoke_canary","outcome":"APPLIED","to":"wait_result"},
        {"from":"wait_result","outcome":"RECEIVED","to":"done"}
    ]
}'::jsonb);

SELECT workflow.activate_flow('workflow-smoke', 1);

SELECT workflow.publish_flow('{
    "contract_version":"course-1",
    "flow_name":"workflow-smoke",
    "version":2,
    "start_step":"invoke_canary",
    "steps":[
        {
            "key":"invoke_canary",
            "type":"automatic",
            "task":{
                "service":"postgres",
                "module":"training",
                "action":"canary",
                "action_version":2,
                "required_policy":["workflow:execute"],
                "timeout_ms":2000,
                "retry":{"max_attempts":2,"delays_ms":[200]},
                "input_mapping":{"/value":"/value"},
                "input_constants":{}
            }
        },
        {"key":"wait_result","type":"wait_signal","signal_type":"training.v2.completed","outcome":"RECEIVED_V2"},
        {"key":"done","type":"end","outcome":"COMPLETED_V2"}
    ],
    "transitions":[
        {"from":"invoke_canary","outcome":"APPLIED_V2","to":"wait_result"},
        {"from":"wait_result","outcome":"RECEIVED_V2","to":"done"}
    ]
}'::jsonb);

SELECT workflow.publish_flow('{
    "contract_version":"course-1",
    "flow_name":"manual-wait",
    "version":1,
    "start_step":"invoke_canary",
    "steps":[
        {
            "key":"invoke_canary",
            "type":"automatic",
            "task":{
                "service":"postgres",
                "module":"training",
                "action":"canary",
                "action_version":1,
                "required_policy":["workflow:execute"],
                "timeout_ms":2000,
                "retry":{"max_attempts":3,"delays_ms":[200,400]},
                "input_mapping":{"/value":"/value"},
                "input_constants":{}
            }
        },
        {"key":"review","type":"manual","allowed_outcomes":["APPROVED"]},
        {"key":"done","type":"end","outcome":"COMPLETED"}
    ],
    "transitions":[
        {"from":"invoke_canary","outcome":"APPLIED","to":"review"},
        {"from":"review","outcome":"APPROVED","to":"done"}
    ]
}'::jsonb);

SELECT workflow.activate_flow('manual-wait', 1);