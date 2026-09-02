CREATE SCHEMA IF NOT EXISTS workflow;

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'workflow_worker') THEN
        CREATE ROLE workflow_worker LOGIN PASSWORD 'worker';
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS workflow.flow_definition (
    flow_name text PRIMARY KEY,
    contract_version text NOT NULL DEFAULT 'course-1' CHECK (contract_version = 'course-1'),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS workflow.flow_version (
    flow_name text NOT NULL REFERENCES workflow.flow_definition(flow_name),
    version integer NOT NULL CHECK (version >= 1),
    status text NOT NULL CHECK (status = 'PUBLISHED'),
    is_active boolean NOT NULL DEFAULT false,
    map jsonb NOT NULL,
    published_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (flow_name, version)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_flow_version_active
    ON workflow.flow_version(flow_name) WHERE is_active;

CREATE TABLE IF NOT EXISTS workflow.step_definition (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    step_key text NOT NULL,
    step_type text NOT NULL CHECK (step_type IN ('AUTOMATIC','WAIT_SIGNAL','MANUAL','END')),
    params jsonb NOT NULL,
    sort_order integer NOT NULL,
    PRIMARY KEY (flow_name, flow_version, step_key),
    FOREIGN KEY (flow_name, flow_version) REFERENCES workflow.flow_version(flow_name, version)
);

CREATE TABLE IF NOT EXISTS workflow.transition_definition (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    from_step_key text NOT NULL,
    outcome text NOT NULL,
    to_step_key text NOT NULL,
    PRIMARY KEY (flow_name, flow_version, from_step_key, outcome),
    FOREIGN KEY (flow_name, flow_version, from_step_key)
        REFERENCES workflow.step_definition(flow_name, flow_version, step_key)
);

CREATE TABLE IF NOT EXISTS workflow.task_definition (
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    step_key text NOT NULL,
    service text NOT NULL CHECK (service = 'postgres'),
    module text NOT NULL,
    action text NOT NULL,
    action_version integer NOT NULL,
    required_policy jsonb NOT NULL,
    timeout_ms integer NOT NULL CHECK (timeout_ms BETWEEN 1 AND 30000),
    retry jsonb NOT NULL,
    input_mapping jsonb NOT NULL,
    input_constants jsonb NOT NULL,
    PRIMARY KEY (flow_name, flow_version, step_key),
    FOREIGN KEY (flow_name, flow_version, step_key)
        REFERENCES workflow.step_definition(flow_name, flow_version, step_key)
);

CREATE TABLE IF NOT EXISTS workflow.process_instance (
    process_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    flow_name text NOT NULL,
    flow_version integer NOT NULL,
    business_key text NOT NULL,
    state text NOT NULL CHECK (state IN ('CREATED','RUNNING','WAITING_SIGNAL','WAITING_MANUAL','COMPLETED','FAILED')),
    current_step_key text,
    data jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    updated_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    UNIQUE (flow_name, business_key),
    FOREIGN KEY (flow_name, flow_version) REFERENCES workflow.flow_version(flow_name, version)
);

CREATE INDEX IF NOT EXISTS ix_process_flow_business ON workflow.process_instance(flow_name, business_key);

CREATE TABLE IF NOT EXISTS workflow.step_instance (
    step_instance_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    step_key text NOT NULL,
    step_type text NOT NULL,
    state text NOT NULL CHECK (state IN ('PENDING','READY','RUNNING','WAITING','COMPLETED','FAILED')),
    outcome text,
    entered_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    completed_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_step_instance_process ON workflow.step_instance(process_id);

CREATE TABLE IF NOT EXISTS workflow.workflow_job (
    job_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    step_instance_id uuid NOT NULL REFERENCES workflow.step_instance(step_instance_id),
    execution_id uuid NOT NULL,
    state text NOT NULL CHECK (state IN ('READY','LEASED','RETRY_WAIT','SUCCEEDED','DEAD')),
    lease_owner text,
    lease_version bigint NOT NULL DEFAULT 0,
    lease_until timestamptz,
    attempt_count integer NOT NULL DEFAULT 0,
    next_attempt_at timestamptz DEFAULT clock_timestamp(),
    created_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS ix_jobs_claimable ON workflow.workflow_job(state, next_attempt_at);
CREATE INDEX IF NOT EXISTS ix_jobs_process ON workflow.workflow_job(process_id);

CREATE TABLE IF NOT EXISTS workflow.task_attempt (
    attempt_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    job_id uuid NOT NULL REFERENCES workflow.workflow_job(job_id),
    execution_id uuid NOT NULL,
    lease_version bigint NOT NULL,
    attempt_number integer NOT NULL CHECK (attempt_number >= 1),
    status text NOT NULL CHECK (status IN ('RUNNING','SUCCEEDED','FAILED','STALE')),
    outcome text,
    error_code text,
    started_at timestamptz NOT NULL DEFAULT clock_timestamp(),
    finished_at timestamptz
);

CREATE INDEX IF NOT EXISTS ix_attempts_job ON workflow.task_attempt(job_id);

CREATE TABLE IF NOT EXISTS workflow.workflow_signal (
    message_id text PRIMARY KEY,
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    signal_type text NOT NULL,
    body_hash text NOT NULL CHECK (body_hash ~ '^[0-9a-f]{64}$'),
    status text NOT NULL CHECK (status IN ('ACCEPTED','APPLIED')),
    received_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_signal_process_type_body
    ON workflow.workflow_signal(process_id, signal_type, body_hash);

CREATE TABLE IF NOT EXISTS workflow.workflow_event (
    event_id uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    process_id uuid NOT NULL REFERENCES workflow.process_instance(process_id),
    step_instance_id uuid,
    event_type text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT clock_timestamp()
);

CREATE INDEX IF NOT EXISTS ix_events_process ON workflow.workflow_event(process_id);

CREATE OR REPLACE FUNCTION workflow.assert_flow_version_immutable()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'manifest.conflict: published flow version is immutable';
    END IF;

    IF NEW.flow_name IS DISTINCT FROM OLD.flow_name
       OR NEW.version IS DISTINCT FROM OLD.version
       OR NEW.status IS DISTINCT FROM OLD.status
       OR NEW.map IS DISTINCT FROM OLD.map
       OR NEW.published_at IS DISTINCT FROM OLD.published_at THEN
        RAISE EXCEPTION 'manifest.conflict: published flow version is immutable';
    END IF;

    IF NEW.is_active IS DISTINCT FROM OLD.is_active THEN
        RETURN NEW;
    END IF;

    NEW.is_active := OLD.is_active;
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_flow_version_immutable_update ON workflow.flow_version;
CREATE TRIGGER trg_flow_version_immutable_update
    BEFORE UPDATE ON workflow.flow_version
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_flow_version_immutable();

DROP TRIGGER IF EXISTS trg_flow_version_immutable_delete ON workflow.flow_version;
CREATE TRIGGER trg_flow_version_immutable_delete
    BEFORE DELETE ON workflow.flow_version
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_flow_version_immutable();

CREATE OR REPLACE FUNCTION workflow.assert_attempt_append_only()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'workflow.append_only: history is append-only';
    END IF;

    IF NEW.job_id IS DISTINCT FROM OLD.job_id
       OR NEW.execution_id IS DISTINCT FROM OLD.execution_id
       OR NEW.lease_version IS DISTINCT FROM OLD.lease_version
       OR NEW.attempt_number IS DISTINCT FROM OLD.attempt_number
       OR NEW.started_at IS DISTINCT FROM OLD.started_at THEN
        RAISE EXCEPTION 'workflow.append_only: history is append-only';
    END IF;

    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_attempt_append_only_update ON workflow.task_attempt;
CREATE TRIGGER trg_attempt_append_only_update
    BEFORE UPDATE ON workflow.task_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_attempt_append_only();

CREATE OR REPLACE FUNCTION workflow.assert_append_only()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    RAISE EXCEPTION 'workflow.append_only: history is append-only';
END;
$$;

DROP TRIGGER IF EXISTS trg_attempt_append_only_delete ON workflow.task_attempt;
CREATE TRIGGER trg_attempt_append_only_delete
    BEFORE DELETE ON workflow.task_attempt
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_append_only();

DROP TRIGGER IF EXISTS trg_event_append_only_update ON workflow.workflow_event;
CREATE TRIGGER trg_event_append_only_update
    BEFORE UPDATE ON workflow.workflow_event
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_append_only();

DROP TRIGGER IF EXISTS trg_event_append_only_delete ON workflow.workflow_event;
CREATE TRIGGER trg_event_append_only_delete
    BEFORE DELETE ON workflow.workflow_event
    FOR EACH ROW EXECUTE FUNCTION workflow.assert_append_only();

CREATE OR REPLACE FUNCTION workflow.touch_process()
RETURNS trigger
LANGUAGE plpgsql
AS $$
BEGIN
    NEW.updated_at := clock_timestamp();
    RETURN NEW;
END;
$$;

DROP TRIGGER IF EXISTS trg_process_touch ON workflow.process_instance;
CREATE TRIGGER trg_process_touch
    BEFORE UPDATE ON workflow.process_instance
    FOR EACH ROW EXECUTE FUNCTION workflow.touch_process();

ALTER FUNCTION workflow.assert_flow_version_immutable() OWNER TO course_owner;
ALTER FUNCTION workflow.assert_attempt_append_only() OWNER TO course_owner;
ALTER FUNCTION workflow.assert_append_only() OWNER TO course_owner;
ALTER FUNCTION workflow.touch_process() OWNER TO course_owner;

REVOKE ALL ON FUNCTION workflow.assert_flow_version_immutable() FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.assert_attempt_append_only() FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.assert_append_only() FROM PUBLIC;
REVOKE ALL ON FUNCTION workflow.touch_process() FROM PUBLIC;

ALTER TABLE workflow.flow_definition OWNER TO course_owner;
ALTER TABLE workflow.flow_version OWNER TO course_owner;
ALTER TABLE workflow.step_definition OWNER TO course_owner;
ALTER TABLE workflow.transition_definition OWNER TO course_owner;
ALTER TABLE workflow.task_definition OWNER TO course_owner;
ALTER TABLE workflow.process_instance OWNER TO course_owner;
ALTER TABLE workflow.step_instance OWNER TO course_owner;
ALTER TABLE workflow.workflow_job OWNER TO course_owner;
ALTER TABLE workflow.task_attempt OWNER TO course_owner;
ALTER TABLE workflow.workflow_signal OWNER TO course_owner;
ALTER TABLE workflow.workflow_event OWNER TO course_owner;
ALTER SCHEMA workflow OWNER TO course_owner;