# ADR-004: Trust boundary и anti-corruption layer в Python-периметре

Статус: принято.

## Контекст

Внешний provider v0.2.0 (выданный Go-образ) говорит на legacy-контракте: принимает `POST /payments` с `operationId` и шлёт callback без JWT и подписи. Внутренняя платформа оперирует versioned receipt v1, подписанным HMAC-SHA256 по точным UTF-8 байтам тела. Три Python-процесса образуют интеграционный периметр между PostgreSQL и provider, но не должны:

- принимать предметные решения (flow, лимит, переход, финальный статус);
- хранить авторитетное состояние;
- иметь прямой доступ к физическим таблицам.

## Решение

1. **Anti-corruption layer.** `receipt-adapter` строго валидирует legacy callback (только известные поля, без CR/LF, размеры), маппит `providerPaymentId -> messageId+providerPaymentId`, `operationId -> externalRequestId`, `result -> outcome`, сохраняет сырую строку `occurredAt`, отбрасывает `message` после валидации. Сериализация — `json.dumps(..., ensure_ascii=False, separators=(",",":"), sort_keys=True)` без BOM/перевода строки; HMAC считается над этими точными байтами.

2. **Входящая подпись проверяется на входе в платформу (C#).** `api` проверяет `X-Provider-Signature: v1=<hex>` над исходными body bytes до предметного вызова, constant-time сравнением, и передаёт в trusted-контекст только маркеры `transport.signatureVerified`, `transport.signatureVersion` и `transport.bodySha256` (SHA-256 exact bytes). Невалидная подпись/версия/формат -> `401 signature.invalid`, target не вызывается. `receipt.accept` без маркера -> `403 receipt.signature_required`, без mutation Inbox. Секрет, полная подпись и тело в контекст не попадают.

3. **Фиксированные SQL-границы.** `outbox-dispatcher` использует роль `outbox_dispatcher` и только `delivery.claim_outbox/succeed_outbox/fail_outbox`; `inbox-reconciler` — `inbox_reconciler` и только `delivery.reconcile_inbox`. Роли не имеют DML, memberships, `CREATE`, `api.invoke` и workflow-finish функций. `receipt-adapter` не имеет PostgreSQL credentials вовсе. Один claim = одна HTTP-попытка; повторы сохраняют `externalRequestId`, тело и correlation.

4. **PostgreSQL остаётся единственным источником истины.** Outbox/Inbox/receipts/decisions живут в БД; provider-simulator может терять ответ или слать callback раньше — дубликаты и конфликты разрешаются предметными инвариантами (`messageId`/`body_hash`), а не памятью Python.

## Последствия

- Python не принимает решений и не владеет состоянием: удаление всех трёх процессов не ломает данные, а recreate продолжает работу по состоянию PostgreSQL.
- Trust boundary отделяет legacy wire-формат provider от versioned внутреннего контракта; подмена adapter target или подделка подписи не достигает доменного target.
- Домен недели 3 (flow, лимит, финальный статус, свидетельства) исполняется generic C#-ядром через `api.invoke` без веток по именам action.