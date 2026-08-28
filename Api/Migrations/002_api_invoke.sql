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

    RETURN v_result;
END;
$$;

ALTER FUNCTION api.invoke(text,text,integer,jsonb,jsonb) OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO course_runtime;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO course_migration;
