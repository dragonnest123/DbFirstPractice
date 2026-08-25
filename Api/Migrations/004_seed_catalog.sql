INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES
('payment','request',1,'POST','payment','request_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationKind","amount","currency"],
    "properties":{
      "operationKind":{"enum":["PAYMENT_EXECUTION","PAYMENT_APPROVAL"]},
      "amount":{"type":"string","pattern":"^(?:0\\.0[1-9]|0\\.[1-9][0-9]?|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"},
      "currency":{"const":"RUB"}
    }
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","requestId","operationKind","amount","currency","status"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "requestId":{"type":"string","minLength":1,"maxLength":128},
      "operationKind":{"enum":["PAYMENT_EXECUTION","PAYMENT_APPROVAL"]},
      "amount":{"type":"string","pattern":"^(?:0\\.0[1-9]|0\\.[1-9][0-9]?|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"},
      "currency":{"const":"RUB"},
      "status":{"enum":["CREATED","PROCESSING","COMPLETED","REJECTED"]}
    }
  }'::jsonb,
 '["CREATED"]'::jsonb,
 '["payment:write"]'::jsonb,
 'required','principal_action',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;

INSERT INTO api.action_catalog(module, action, version, http_method, target_schema, target_function, request_schema, response_schema, outcomes, required_policy, idempotency_mode, idempotency_scope, timeout_ms, enabled, is_default, contract_version)
VALUES
('operation','get',1,'POST','payment','get_v1',
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId"],
    "properties":{"operationId":{"type":"string","format":"uuid"}}
  }'::jsonb,
 '{
    "$schema":"https://json-schema.org/draft/2020-12/schema",
    "type":"object","additionalProperties":false,
    "required":["operationId","requestId","operationKind","amount","currency","status"],
    "properties":{
      "operationId":{"type":"string","format":"uuid"},
      "requestId":{"type":"string","minLength":1,"maxLength":128},
      "operationKind":{"enum":["PAYMENT_EXECUTION","PAYMENT_APPROVAL"]},
      "amount":{"type":"string","pattern":"^(?:0\\.0[1-9]|0\\.[1-9][0-9]?|[1-9][0-9]{0,15}(?:\\.[0-9]{1,2})?)$"},
      "currency":{"const":"RUB"},
      "status":{"enum":["CREATED","PROCESSING","COMPLETED","REJECTED"]}
    }
  }'::jsonb,
 '["FOUND"]'::jsonb,
 '["payment:read"]'::jsonb,
 'none','none',2000,true,true,'course-1')
ON CONFLICT (module,action,version) DO NOTHING;
