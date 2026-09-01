CREATE OR REPLACE FUNCTION payment.request_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment, api
AS $$
DECLARE
    v_request_id text := p_context ->> 'requestId';
    v_correlation text := p_context ->> 'correlationId';
    v_principal text := p_context ->> 'principal';
    v_operation_kind text := p_payload ->> 'operationKind';
    v_amount text := p_payload ->> 'amount';
    v_currency text := p_payload ->> 'currency';
    v_amount_num numeric(20,2);
    v_operation_id uuid;
    v_payload_hash text;
BEGIN
    IF v_request_id IS NULL OR v_request_id = '' THEN
        RETURN jsonb_build_object('status','error','code','idempotency.required','message','missing Idempotency-Key','retryable',false,'details','{}'::jsonb,'meta',jsonb_build_object('correlationId',v_correlation,'actionVersion',1));
    END IF;

    BEGIN
        v_amount_num := v_amount::numeric;
    EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object('status','error','code','payload.invalid','message','invalid amount','retryable',false,'details','{}'::jsonb,'meta',jsonb_build_object('correlationId',v_correlation,'actionVersion',1));
    END;

    v_payload_hash := encode(digest(p_payload::text, 'sha256'), 'hex');

    v_operation_id := gen_random_uuid();

    INSERT INTO payment.operations(operation_id, request_id, principal, operation_kind, amount, currency, status, process_id)
    VALUES (v_operation_id, v_request_id, v_principal, v_operation_kind, v_amount_num, v_currency, 'CREATED', NULL);

    INSERT INTO payment.operation_events(event_id, operation_id, event_type, payload_hash)
    VALUES (gen_random_uuid(), v_operation_id, 'OPERATION_CREATED', v_payload_hash);

    RETURN jsonb_build_object(
        'status','ok',
        'outcome','CREATED',
        'result', jsonb_build_object(
            'operationId', v_operation_id::text,
            'requestId', v_request_id,
            'operationKind', v_operation_kind,
            'amount', v_amount,
            'currency', v_currency,
            'status','CREATED'
        ),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1)
    );
END;
$$;

CREATE OR REPLACE FUNCTION payment.get_v1(p_context jsonb, p_payload jsonb)
RETURNS jsonb
LANGUAGE plpgsql
SECURITY DEFINER
SET search_path = pg_catalog, payment
AS $$
DECLARE
    v_correlation text := p_context ->> 'correlationId';
    v_op_id text := p_payload ->> 'operationId';
    v_op payment.operations%ROWTYPE;
BEGIN
    BEGIN
        v_op.operation_id := v_op_id::uuid;
    EXCEPTION WHEN OTHERS THEN
        RETURN jsonb_build_object('status','error','code','payload.invalid','message','invalid operationId','retryable',false,'details','{}'::jsonb,'meta',jsonb_build_object('correlationId',v_correlation,'actionVersion',1));
    END;

    SELECT * INTO v_op FROM payment.operations WHERE operation_id = v_op_id::uuid;
    IF NOT FOUND THEN
        RETURN jsonb_build_object('status','error','code','operation.not_found','message','operation not found','retryable',false,'details','{}'::jsonb,'meta',jsonb_build_object('correlationId',v_correlation,'actionVersion',1));
    END IF;

    RETURN jsonb_build_object(
        'status','ok',
        'outcome','FOUND',
        'result', jsonb_build_object(
            'operationId', v_op.operation_id::text,
            'requestId', v_op.request_id,
            'operationKind', v_op.operation_kind,
            'amount', v_op.amount::text,
            'currency', v_op.currency,
            'status', v_op.status
        ),
        'meta', jsonb_build_object('correlationId', v_correlation, 'actionVersion', 1)
    );
END;
$$;

ALTER FUNCTION payment.request_v1(jsonb,jsonb) OWNER TO course_target;
ALTER FUNCTION payment.get_v1(jsonb,jsonb) OWNER TO course_target;
GRANT EXECUTE ON FUNCTION payment.request_v1(jsonb,jsonb) TO course_owner;
GRANT EXECUTE ON FUNCTION payment.get_v1(jsonb,jsonb) TO course_owner;
REVOKE ALL ON FUNCTION payment.request_v1(jsonb,jsonb) FROM PUBLIC;
REVOKE ALL ON FUNCTION payment.get_v1(jsonb,jsonb) FROM PUBLIC;
