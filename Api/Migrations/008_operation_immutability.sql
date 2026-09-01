CREATE OR REPLACE FUNCTION payment.assert_operation_immutable()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'operation.immutable: operation cannot be deleted';
    END IF;

    IF NEW.operation_id IS DISTINCT FROM OLD.operation_id
       OR NEW.request_id IS DISTINCT FROM OLD.request_id
       OR NEW.principal IS DISTINCT FROM OLD.principal
       OR NEW.operation_kind IS DISTINCT FROM OLD.operation_kind
       OR NEW.amount IS DISTINCT FROM OLD.amount
       OR NEW.currency IS DISTINCT FROM OLD.currency
       OR NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'operation.immutable: identity and payload fields are immutable';
    END IF;

    NEW.updated_at := clock_timestamp();
    RETURN NEW;
END;
$$;

CREATE OR REPLACE FUNCTION payment.assert_event_append_only()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'event.immutable: operation events are append-only';
END;
$$;

DROP TRIGGER IF EXISTS trg_operation_immutable_update ON payment.operations;
CREATE TRIGGER trg_operation_immutable_update
    BEFORE UPDATE ON payment.operations
    FOR EACH ROW EXECUTE FUNCTION payment.assert_operation_immutable();

DROP TRIGGER IF EXISTS trg_operation_immutable_delete ON payment.operations;
CREATE TRIGGER trg_operation_immutable_delete
    BEFORE DELETE ON payment.operations
    FOR EACH ROW EXECUTE FUNCTION payment.assert_operation_immutable();

DROP TRIGGER IF EXISTS trg_event_append_only_update ON payment.operation_events;
CREATE TRIGGER trg_event_append_only_update
    BEFORE UPDATE ON payment.operation_events
    FOR EACH ROW EXECUTE FUNCTION payment.assert_event_append_only();

DROP TRIGGER IF EXISTS trg_event_append_only_delete ON payment.operation_events;
CREATE TRIGGER trg_event_append_only_delete
    BEFORE DELETE ON payment.operation_events
    FOR EACH ROW EXECUTE FUNCTION payment.assert_event_append_only();

ALTER FUNCTION payment.assert_operation_immutable() OWNER TO course_owner;
ALTER FUNCTION payment.assert_event_append_only() OWNER TO course_owner;
REVOKE ALL ON FUNCTION payment.assert_operation_immutable() FROM PUBLIC;
REVOKE ALL ON FUNCTION payment.assert_event_append_only() FROM PUBLIC;