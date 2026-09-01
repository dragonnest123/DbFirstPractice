CREATE OR REPLACE FUNCTION api.publish_action(p_manifest jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, api, pg_temp
AS $$
DECLARE
    v_module text := p_manifest ->> 'module';
    v_action text := p_manifest ->> 'action';
    v_version integer := (p_manifest ->> 'version')::integer;
    v_existing api.action_catalog%ROWTYPE;
    v_equal boolean;
BEGIN
    IF NOT (p_manifest ? 'module' AND p_manifest ? 'action' AND p_manifest ? 'version'
            AND p_manifest ? 'target_schema' AND p_manifest ? 'target_function'
            AND p_manifest ? 'request_schema' AND p_manifest ? 'response_schema'
            AND p_manifest ? 'outcomes' AND p_manifest ? 'required_policy') THEN
        RAISE EXCEPTION 'manifest.invalid: manifest does not match contract';
    END IF;

    IF p_manifest ->> 'contract_version' <> 'course-1'
       OR p_manifest ->> 'http_method' <> 'POST'
       OR v_module !~ '^[a-z][a-z0-9_]{0,62}$'
       OR v_action !~ '^[a-z][a-z0-9_]{0,62}$'
       OR v_version < 1
       OR (p_manifest ->> 'timeout_ms')::integer NOT BETWEEN 1 AND 30000
       OR p_manifest ->> 'idempotency_mode' NOT IN ('none','optional','required')
       OR p_manifest ->> 'idempotency_scope' NOT IN ('none','principal_action','consumer_action','global_action')
       OR (p_manifest ->> 'idempotency_mode') = 'none' AND (p_manifest ->> 'idempotency_scope') <> 'none'
       OR (p_manifest ->> 'is_default')::boolean AND NOT (p_manifest ->> 'enabled')::boolean THEN
        RAISE EXCEPTION 'manifest.invalid: manifest does not match contract';
    END IF;

    SELECT * INTO v_existing
    FROM api.action_catalog
    WHERE module = v_module AND action = v_action AND version = v_version;

    IF FOUND THEN
        v_equal := v_existing.contract_version = p_manifest ->> 'contract_version'
            AND v_existing.http_method = p_manifest ->> 'http_method'
            AND v_existing.target_schema = p_manifest ->> 'target_schema'
            AND v_existing.target_function = p_manifest ->> 'target_function'
            AND v_existing.request_schema = p_manifest -> 'request_schema'
            AND v_existing.response_schema = p_manifest -> 'response_schema'
            AND v_existing.outcomes = p_manifest -> 'outcomes'
            AND v_existing.required_policy = p_manifest -> 'required_policy'
            AND v_existing.idempotency_mode = p_manifest ->> 'idempotency_mode'
            AND v_existing.idempotency_scope = p_manifest ->> 'idempotency_scope'
            AND v_existing.timeout_ms = (p_manifest ->> 'timeout_ms')::integer
            AND v_existing.enabled = COALESCE((p_manifest ->> 'enabled')::boolean, false)
            AND v_existing.is_default = COALESCE((p_manifest ->> 'is_default')::boolean, false);

        IF v_equal THEN
            RETURN jsonb_build_object('status','exists');
        END IF;

        RAISE EXCEPTION 'manifest.conflict: published action version is immutable';
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_proc p
        JOIN pg_namespace n ON n.oid = p.pronamespace
        WHERE n.nspname = p_manifest ->> 'target_schema'
          AND p.proname = p_manifest ->> 'target_function'
          AND p.pronargs = 2
          AND p.proargtypes[0] = 'jsonb'::regtype::oid
          AND p.proargtypes[1] = 'jsonb'::regtype::oid
          AND p.prorettype = 'jsonb'::regtype::oid
          AND p.proowner = 'course_target'::regrole::oid
    ) THEN
        RAISE EXCEPTION 'manifest.invalid: target function is not owned by course_target';
    END IF;

    INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function,
        request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope,
        timeout_ms, enabled, is_default, contract_version)
    VALUES (v_module, v_action, v_version, p_manifest ->> 'http_method', p_manifest ->> 'target_schema',
        p_manifest ->> 'target_function', p_manifest -> 'request_schema', p_manifest -> 'response_schema',
        p_manifest -> 'outcomes', p_manifest -> 'required_policy', p_manifest ->> 'idempotency_mode',
        p_manifest ->> 'idempotency_scope', (p_manifest ->> 'timeout_ms')::integer,
        COALESCE((p_manifest ->> 'enabled')::boolean, false), COALESCE((p_manifest ->> 'is_default')::boolean, false),
        p_manifest ->> 'contract_version');

    RETURN jsonb_build_object('status','published');
EXCEPTION
    WHEN unique_violation THEN
        RAISE EXCEPTION 'manifest.conflict: route already has a default version';
    WHEN others THEN
        IF SQLERRM LIKE 'manifest.%' THEN
            RAISE;
        END IF;
        RAISE EXCEPTION 'manifest.invalid: %', SQLERRM;
END;
$$;

CREATE OR REPLACE FUNCTION api.activate_action(p_module text, p_action text, p_version integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, api, pg_temp
AS $$
BEGIN
    IF p_module !~ '^[a-z][a-z0-9_]{0,62}$' OR p_action !~ '^[a-z][a-z0-9_]{0,62}$' OR p_version < 1 THEN
        RAISE EXCEPTION 'action.invalid: invalid route';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM api.action_catalog WHERE module = p_module AND action = p_action AND version = p_version) THEN
        RAISE EXCEPTION 'action.not_found: version % of %.% is not published', p_version, p_module, p_action;
    END IF;

    UPDATE api.action_catalog SET is_default = false WHERE module = p_module AND action = p_action;
    UPDATE api.action_catalog SET enabled = true, is_default = true
    WHERE module = p_module AND action = p_action AND version = p_version;

    RETURN jsonb_build_object('status','activated');
END;
$$;

CREATE OR REPLACE FUNCTION api.disable_action(p_module text, p_action text, p_version integer, p_replacement integer)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, api, pg_temp
AS $$
BEGIN
    IF p_module !~ '^[a-z][a-z0-9_]{0,62}$' OR p_action !~ '^[a-z][a-z0-9_]{0,62}$' OR p_version < 1 THEN
        RAISE EXCEPTION 'action.invalid: invalid route';
    END IF;

    IF NOT EXISTS (SELECT 1 FROM api.action_catalog WHERE module = p_module AND action = p_action AND version = p_version) THEN
        RAISE EXCEPTION 'action.not_found: version % of %.% is not published', p_version, p_module, p_action;
    END IF;

    IF p_replacement IS NOT NULL THEN
        IF p_replacement = p_version
           OR NOT EXISTS (SELECT 1 FROM api.action_catalog
                          WHERE module = p_module AND action = p_action AND version = p_replacement AND enabled) THEN
            RAISE EXCEPTION 'action.invalid: replacement version not found or disabled';
        END IF;

        UPDATE api.action_catalog SET is_default = false WHERE module = p_module AND action = p_action;
        UPDATE api.action_catalog SET enabled = true, is_default = true
        WHERE module = p_module AND action = p_action AND version = p_replacement;
    END IF;

    UPDATE api.action_catalog SET enabled = false
    WHERE module = p_module AND action = p_action AND version = p_version;

    RETURN jsonb_build_object('status','disabled');
END;
$$;

REVOKE ALL ON FUNCTION api.publish_action(jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.activate_action(text,text,integer) FROM PUBLIC;
REVOKE ALL ON FUNCTION api.disable_action(text,text,integer,integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION api.publish_action(jsonb) TO course_publication;
GRANT EXECUTE ON FUNCTION api.activate_action(text,text,integer) TO course_publication;
GRANT EXECUTE ON FUNCTION api.disable_action(text,text,integer,integer) TO course_publication;
GRANT EXECUTE ON FUNCTION api.publish_action(jsonb) TO course_migration;
GRANT EXECUTE ON FUNCTION api.activate_action(text,text,integer) TO course_migration;
GRANT EXECUTE ON FUNCTION api.disable_action(text,text,integer,integer) TO course_migration;

ALTER FUNCTION api.publish_action(jsonb) OWNER TO course_owner;
ALTER FUNCTION api.activate_action(text,text,integer) OWNER TO course_owner;
ALTER FUNCTION api.disable_action(text,text,integer,integer) OWNER TO course_owner;