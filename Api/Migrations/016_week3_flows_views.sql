-- Week 3: flow maps and stable autocheck views.

SELECT workflow.publish_flow('{
    "contract_version":"course-1",
    "flow_name":"payment-processing",
    "version":1,
    "start_step":"validate_operation",
    "steps":[
        {"key":"validate_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"validate","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":1,"delays_ms":[]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"prepare_external_request","type":"automatic","task":{"service":"postgres","module":"payment","action":"prepare_external","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":3,"delays_ms":[200,400]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"wait_receipt","type":"wait_signal","signal_type":"payment.receipt","outcome":"RECEIVED"},
        {"key":"apply_receipt","type":"automatic","task":{"service":"postgres","module":"payment","action":"apply_receipt","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":2,"delays_ms":[200]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"complete_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"complete","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":2,"delays_ms":[200]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"reject_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"reject","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":2,"delays_ms":[200]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"done","type":"end","outcome":"FINISHED"}
    ],
    "transitions":[
        {"from":"validate_operation","outcome":"VALID","to":"prepare_external_request"},
        {"from":"prepare_external_request","outcome":"PREPARED","to":"wait_receipt"},
        {"from":"wait_receipt","outcome":"RECEIVED","to":"apply_receipt"},
        {"from":"apply_receipt","outcome":"COMPLETED","to":"complete_operation"},
        {"from":"apply_receipt","outcome":"REJECTED","to":"reject_operation"},
        {"from":"complete_operation","outcome":"COMPLETED","to":"done"},
        {"from":"reject_operation","outcome":"REJECTED","to":"done"}
    ]
}'::jsonb);

SELECT workflow.activate_flow('payment-processing', 1);

SELECT workflow.publish_flow('{
    "contract_version":"course-1",
    "flow_name":"payment-review",
    "version":1,
    "start_step":"validate_operation",
    "steps":[
        {"key":"validate_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"validate","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":1,"delays_ms":[]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"check_limit","type":"automatic","task":{"service":"postgres","module":"payment","action":"check_limit","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":1,"delays_ms":[]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"wait_manual_decision","type":"manual","allowed_outcomes":["APPROVED","REJECTED"]},
        {"key":"approve_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"approve","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":2,"delays_ms":[200]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"reject_operation","type":"automatic","task":{"service":"postgres","module":"payment","action":"reject","action_version":1,"required_policy":["payment:internal"],"timeout_ms":2000,"retry":{"max_attempts":2,"delays_ms":[200]},"input_mapping":{"/operationId":"/operationId"},"input_constants":{}}},
        {"key":"done","type":"end","outcome":"FINISHED"}
    ],
    "transitions":[
        {"from":"validate_operation","outcome":"VALID","to":"check_limit"},
        {"from":"check_limit","outcome":"WITHIN_LIMIT","to":"approve_operation"},
        {"from":"check_limit","outcome":"REVIEW_REQUIRED","to":"wait_manual_decision"},
        {"from":"wait_manual_decision","outcome":"APPROVED","to":"approve_operation"},
        {"from":"wait_manual_decision","outcome":"REJECTED","to":"reject_operation"},
        {"from":"approve_operation","outcome":"APPROVED","to":"done"},
        {"from":"reject_operation","outcome":"REJECTED","to":"done"}
    ]
}'::jsonb);

SELECT workflow.activate_flow('payment-review', 1);

CREATE OR REPLACE VIEW autocheck.external_requests AS
SELECT external_request_id::text, operation_id::uuid, state::text, payload_hash::text, created_at::timestamptz
FROM delivery.external_request;

CREATE OR REPLACE VIEW autocheck.receipts AS
SELECT message_id::text, external_request_id::text, message_version::integer, outcome::text,
       signature_valid::boolean, body_hash::text, received_at::timestamptz, applied_at::timestamptz
FROM delivery.receipt;

CREATE OR REPLACE VIEW autocheck.outbox AS
SELECT outbox_id::uuid, external_request_id::text, state::text, attempt_count::integer,
       next_attempt_at::timestamptz, last_error_code::text, created_at::timestamptz, delivered_at::timestamptz
FROM delivery.outbox;

CREATE OR REPLACE VIEW autocheck.inbox AS
SELECT message_id::text, body_hash::text, state::text, received_at::timestamptz, applied_at::timestamptz
FROM delivery.inbox;

CREATE OR REPLACE VIEW autocheck.decisions AS
SELECT decision_id::uuid, process_id::uuid, step_instance_id::uuid, source::text, principal::text,
       reason_hash::text, outcome::text, rule_version::text, created_at::timestamptz
FROM payment.decisions;

ALTER VIEW autocheck.external_requests OWNER TO course_owner;
ALTER VIEW autocheck.receipts OWNER TO course_owner;
ALTER VIEW autocheck.outbox OWNER TO course_owner;
ALTER VIEW autocheck.inbox OWNER TO course_owner;
ALTER VIEW autocheck.decisions OWNER TO course_owner;

GRANT SELECT ON autocheck.external_requests, autocheck.receipts, autocheck.outbox,
    autocheck.inbox, autocheck.decisions TO course_runtime;