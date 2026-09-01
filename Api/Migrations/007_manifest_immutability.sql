CREATE OR REPLACE FUNCTION api.assert_manifest_immutable()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'manifest.conflict: published action version is immutable';
    END IF;

    IF NEW.contract_version IS DISTINCT FROM OLD.contract_version
       OR NEW.module IS DISTINCT FROM OLD.module
       OR NEW.action IS DISTINCT FROM OLD.action
       OR NEW.version IS DISTINCT FROM OLD.version
       OR NEW.http_method IS DISTINCT FROM OLD.http_method
       OR NEW.target_schema IS DISTINCT FROM OLD.target_schema
       OR NEW.target_function IS DISTINCT FROM OLD.target_function
       OR NEW.request_schema IS DISTINCT FROM OLD.request_schema
       OR NEW.response_schema IS DISTINCT FROM OLD.response_schema
       OR NEW.outcomes IS DISTINCT FROM OLD.outcomes
       OR NEW.required_policy IS DISTINCT FROM OLD.required_policy
       OR NEW.idempotency_mode IS DISTINCT FROM OLD.idempotency_mode
       OR NEW.idempotency_scope IS DISTINCT FROM OLD.idempotency_scope
       OR NEW.timeout_ms IS DISTINCT FROM OLD.timeout_ms THEN
        RAISE EXCEPTION 'manifest.conflict: published action version is immutable';
    END IF;

    NEW.created_at := OLD.created_at;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_manifest_immutable_update ON api.action_catalog;
CREATE TRIGGER trg_manifest_immutable_update
    BEFORE UPDATE ON api.action_catalog
    FOR EACH ROW EXECUTE FUNCTION api.assert_manifest_immutable();

DROP TRIGGER IF EXISTS trg_manifest_immutable_delete ON api.action_catalog;
CREATE TRIGGER trg_manifest_immutable_delete
    BEFORE DELETE ON api.action_catalog
    FOR EACH ROW EXECUTE FUNCTION api.assert_manifest_immutable();

ALTER FUNCTION api.assert_manifest_immutable() OWNER TO course_owner;
REVOKE ALL ON FUNCTION api.assert_manifest_immutable() FROM PUBLIC;