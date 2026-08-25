# ADR-002: Технический и предметный результат

Статус: принято.

## Контекст

Один HTTP-вызов `POST /api/{module}/{action}` порождает два разных класса результатов:

- технический — запись dispatch, корреляция, идемпотентность, код ответа;
- предметный — создание operation и события `OPERATION_CREATED` внутри target-функции.

Нельзя коммитить предметный эффект, если контрактный result не прошёл проверки, и нельзя смешивать оба результата в одном envelope.

## Решение

1. Target-функция возвращает единый envelope `{status, outcome, result, meta}`; предметное состояние (operations/events/idempotency) она меняет в той же транзакции, что и вызов `api.invoke`.
2. C#-слой после `api.invoke` проверяет: `status=ok`, `outcome` принадлежит `manifest.outcomes`, `result` валиден по `response_schema`; только затем `INSERT action_dispatches(OK)` и `COMMIT`.
3. `status=error`, неизвестный outcome или невалидный result -> `ROLLBACK` всего предметного эффекта; ERROR-dispatch пишется отдельной (best-effort) записью после отката.
4. `api.action_dispatches` хранит только `payload_hash` (sha256 lowercase) — полный payload, JWT и секреты в проекциях не публикуются.
5. Идемпотентный повтор возвращает сохранённый `response` из `api.idempotency_store` без повторного предметного эффекта; тот же key с другим payload -> `409 idempotency.conflict`.

## Последствия

- operation и событие не расходятся: они создаются атомарно в одной транзакции.
- Гарантия «один ключ — один предметный эффект» обеспечивается уникальностью PostgreSQL, а не process-local lock.