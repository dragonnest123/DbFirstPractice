CREATE OR REPLACE VIEW autocheck.contract_info AS
SELECT 'course-1'::text AS contract_version, now()::timestamptz AS generated_at;

CREATE OR REPLACE VIEW autocheck.action_definitions AS
SELECT module::text, action::text, version::int, http_method::text, target_schema::text, target_function::text, outcomes::jsonb, enabled::boolean, is_default::boolean
FROM api.action_catalog;

CREATE OR REPLACE VIEW autocheck.action_dispatches AS
SELECT correlation_id::uuid AS correlation_id, request_id::text, module::text, action::text, version::int, principal::text, payload_hash::text, status::text, outcome::text, occurred_at::timestamptz
FROM api.action_dispatches;

CREATE OR REPLACE VIEW autocheck.operations AS
SELECT operation_id::uuid, request_id::text, operation_kind::text, amount::numeric, currency::text, status::text, process_id::uuid, created_at::timestamptz, updated_at::timestamptz
FROM payment.operations;

CREATE OR REPLACE VIEW autocheck.operation_events AS
SELECT event_id::uuid, operation_id::uuid, event_type::text, payload_hash::text, occurred_at::timestamptz
FROM payment.operation_events;

REVOKE ALL ON SCHEMA payment FROM course_runtime;
GRANT USAGE ON SCHEMA api, autocheck TO course_runtime;
GRANT SELECT ON autocheck.contract_info, autocheck.action_definitions, autocheck.action_dispatches, autocheck.operations, autocheck.operation_events TO course_runtime;
GRANT SELECT ON api.action_catalog TO course_runtime;
GRANT INSERT ON api.action_dispatches TO course_runtime;
GRANT SELECT, INSERT, UPDATE ON api.idempotency_store TO course_runtime;
GRANT EXECUTE ON FUNCTION api.invoke(text,text,integer,jsonb,jsonb) TO course_runtime;

REVOKE ALL ON payment.operations, payment.operation_events FROM course_runtime;
REVOKE ALL ON payment.operations, payment.operation_events FROM PUBLIC;

GRANT USAGE ON SCHEMA public, api, payment, autocheck TO course_migration;
GRANT ALL ON public.schema_migrations, api.action_catalog, api.action_dispatches, api.idempotency_store, payment.operations, payment.operation_events TO course_migration;

ALTER VIEW autocheck.contract_info OWNER TO course_owner;
ALTER VIEW autocheck.action_definitions OWNER TO course_owner;
ALTER VIEW autocheck.action_dispatches OWNER TO course_owner;
ALTER VIEW autocheck.operations OWNER TO course_owner;
ALTER VIEW autocheck.operation_events OWNER TO course_owner;
ALTER TABLE api.action_catalog OWNER TO course_owner;
ALTER TABLE api.action_dispatches OWNER TO course_owner;
ALTER TABLE api.idempotency_store OWNER TO course_owner;
ALTER TABLE payment.operations OWNER TO course_owner;
ALTER TABLE payment.operation_events OWNER TO course_owner;
ALTER SCHEMA api OWNER TO course_owner;
ALTER SCHEMA payment OWNER TO course_owner;
ALTER SCHEMA autocheck OWNER TO course_owner;
