# ADR-003: Lease, fencing и at-least-once исполнение jobs

Статус: принято.

## Контекст

Workflow-ядро исполняет автоматические шаги на двух worker-инстансах одного image. При сбое между claim и finish процесс не должен терять эффект, а stale worker не должен завершать job, который уже перешел другому worker. Требуется:

- ровно один предметный эффект на одну логическую работу (at-least-once по эффекту, не более одного коммита finish);
- восстановление после crash: reclaim истёкшей lease, отклонение stale completion;
- append-only attempts/events без удаления и перезаписи идентичности.

## Решение

1. **Claim как атомарная короткая транзакция.** `workflow.claim_jobs(owner, batch, lease_ms)` выбирает готовые jobs `FOR UPDATE SKIP LOCKED`, переводит `READY -> LEASED`, инкрементирует `lease_version` и `attempt_count`, создаёт `task_attempt(RUNNING)` и возвращает pinned task/action данные одним вызовом. Никакая блокировка не удерживается на время исполнения action.

2. **Идентификаторы.** `jobId` — логическая работа; `executionId` — стабильный idempotency-ключ предметного эффекта всех retry одной job; `attemptId` — конкретная попытка; `leaseVersion` — возрастающее право на finish. Retry меняет только `attemptId`/`leaseVersion`.

3. **Fencing на finish.** `workflow.finish_job(jobId, owner, leaseVersion, outcome, result)` повторно проверяет `state=LEASED`, owner и `leaseVersion`; любое расхождение -> `workflow.lease_stale` без изменений данных. `workflow.fail_job` использует ту же границу.

4. **At-least-once через единую транзакцию.** Worker выполняет `api.invoke` и `workflow.finish_job` в одной Npgsql-транзакции: предметный эффект и продвижение процесса фиксируются атомарно (включая dispatch и следующую job). Crash до commit -> rollback нулевого эффекта, reclaim повторяет попытку. Error/contract violation -> rollback и отдельный `fail_job` с bounded retry-расписанием; исчерпание -> `DEAD`/`FAILED` + событие `TaskFailed`.

5. **Идемпотентность эффекта.** `executionId` передаётся в trusted-контекст (`api.invoke`) как `requestId`/`correlationId`; предметные target-функции используют его как idempotency-ключ (например, `ON CONFLICT DO NOTHING`), поэтому даже повторный вызов после reclaim не создаёт дубликат.

6. **Append-only история.** `workflow_event` — только INSERT; `task_attempt` — только INSERT + терминальный переход статуса (identity-поля неизменяемы). Изменение/удаление строк отклоняется триггером.

## Последствия

- Два worker не создают два эффекта одной job: победителя определяет атомарный claim, stale finish отклонён fencing-условием.
- Crash после claim -> reclaim после expiry; crash между action и finish -> ноль partial effects.
- `workflow_worker` не имеет прямого DML: все изменения — через SECURITY DEFINER функции `workflow.claim_jobs`/`finish_job`/`fail_job` и `api.invoke`.