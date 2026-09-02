# NewProject — Week 2. Персистентное workflow-ядро

## Решение

### Архитектура

Контур состоит из шести сервисов `compose.yaml`:

```text
Client http://localhost:8080
  -> gateway (C# ASP.NET Core, whitelist-прокси, единственный опубликованный порт 8080)
  -> api      (C# action runtime, внутренний, без опубликованных портов)
  -> postgres (PostgreSQL 16, база course, named volume pgdata; миграции встроены в image)
  -> worker-a / worker-b (общий C# image Workflow.Worker, lease owners worker-a/worker-b)
cli (C#) -> postgres (migration apply, action/flow publish/activate, flow start/get/signal)
```

Единственная точка предметного выполнения — PostgreSQL-функция `api.invoke(...)` (SECURITY DEFINER, владелец `course_owner` NOLOGIN). C#-слой `api` выполняет JWT-аутентификацию, резолв action из immutable каталога `api.action_catalog`, валидацию request/response схем и управляет одной транзакцией вокруг `api.invoke`. Новые actions регистрируются манифестом через `cli` без пересборки `gateway`/`api`.

Workflow-ядро недели 2 исполняет произвольные карты `course-1`: worker забирает jobs через `workflow.claim_jobs`, вызывает закреплённый action через `api.invoke` в trusted-контексте `workflow-worker` (principal, processId, jobId, executionId, attemptId, deadline) и завершает через `workflow.finish_job` в одной транзакции с эффектом. Публикация новых action и карт после сборки не требует изменения C#. Подробная схема контейнеров: [C4](docs/c4.puml). Решения по границам доверия: [ADR-001](docs/adr-001-trust-boundary.md), [ADR-002](docs/adr-002-technical-vs-domain-result.md), [ADR-003](docs/adr-003-lease-fencing.md).

### Запуск

Prerequisites: Docker с Docker Compose v2, Python 3 для открытой проверки.

```bash
docker compose up -d --build
```

После старта без ручных SQL-команд доступны:

- `POST http://localhost:8080/api/payment/request` — создать операцию (JWT + Idempotency-Key);
- `POST http://localhost:8080/api/operation/get` — прочитать операцию;
- `POST http://localhost:8080/api/workflow/get` — полное состояние процесса (policy `workflow:read`);
- `GET http://localhost:8080/health/live`, `/health/ready`, `/openapi/default.json`;
- `./course.sh flow start workflow-smoke --business-key <key> --data <file>` — запустить smoke-процесс.

Проверка:

```bash
./check.sh
```

### Workflow-карты

Карта `course-1` — JSON (или YAML) документ с `flow_name`, immutable `version`, `start_step`, шагами и переходами:

| Тип шага | Поведение |
|---|---|
| `automatic` | создаёт job и вызывает зарегистрированный action через `api.invoke` |
| `wait_signal` | долговечно ждёт идемпотентный сигнал (`flow signal`) |
| `manual` | долговечно ждёт решение (на неделе 2 — до `WAITING_MANUAL`) |
| `end` | завершает процесс с объявленным outcome |

Переходы — по конечному `outcome` (exclusive routing). Публикация создаёт immutable `flow_version`; `flow activate` выбирает активную версию для новых процессов, уже запущенные остаются pinned. Встроенные карты: `workflow-smoke` v1 (`automatic → wait_signal → end`, action `training.canary` v1) и v2 (action v2, другой сигнал и outcome — доказуемо другой `flow_version`), `manual-wait` (до `WAITING_MANUAL`). Пример: [workflow-map.example.json](task/week2/08_program_and_contracts/contracts/course-1/workflow-map.example.json), схема: [workflow-map.schema.json](task/week2/08_program_and_contracts/contracts/course-1/workflow-map.schema.json).

CLI: `flow validate/publish/list/activate/start/get/signal`; `flow test-finish` доступен только при `COURSE_TEST_PROFILE=1` и вызывает production finish-границу. Validator проверяет JSON Schema, граф (один start, достижимость, ацикличность, покрытие outcomes), соответствие action и policy, JSON Pointer mapping (RFC 6901) и bounded retry.

### Worker

C# `Workflow.Worker` (`Worker/`) — generic исполнитель. Цикл:

```text
workflow.claim_jobs(owner, batch, leaseMs)  -> FOR UPDATE SKIP LOCKED, leaseVersion++, attempt
  -> payload из input_constants + input_mapping (JSON Pointer)
  -> BEGIN; api.invoke(trusted context); валидация envelope/outcome/result;
     workflow.finish_job(jobId, owner, leaseVersion, outcome, result); COMMIT
  -> ошибка: rollback + отдельный workflow.fail_job (retry schedule или DEAD/FAILED + TaskFailed)
```

Lease/fencing: stale finish отклоняется (`workflow.lease_stale`), reclaim сохраняет `jobId`/`executionId` и создаёт новый `attemptId`/`leaseVersion`. Роль `workflow_worker` имеет EXECUTE ровно на `workflow.claim_jobs`, `api.invoke`, `workflow.finish_job`, `workflow.fail_job` и не имеет прямого DML. Failpoints (`COURSE_FAILPOINT`): `after_job_claim` и `after_action_before_finish` — worker пишет structured log `{"event":"failpoint.reached",...}` и блокируется до остановки. Конфигурация: `COURSE_WORKER_OWNER`, `COURSE_LEASE_MS` (test profile 2000), `COURSE_POLL_INTERVAL_MS` (test profile 100).

### Проверка

```bash
./check.sh
```

Открытая проверка недели 2 собирает контур с `--pull --no-cache`, поднимает стек без rebuild, применяет фикстуры (миграция, action v1/v2, карты v1/v2, invalid maps), гоняет publication/execution/versioning/concurrency/recovery/resilience/integrity сценарии и пишет `week-2-public-report.json`. Собственные regression tests (нужен запущенный Docker):

```bash
dotnet test Api.Tests          # unit: error mapping, envelope, HTTP statuses
dotnet test Api.IntegrationTests # integration: publication conflicts, privileges, append-only history, domain errors через HTTP, workflow (publish/activate/start/claim/finish/fail/signal/get, lease/fencing, retry/DEAD) на Testcontainers PostgreSQL (Testcontainers PostgreSQL + миграции 001..013)
```

На Windows `./check.sh` использует `compose-wrapper.sh`: он адаптирует MSYS-окружение (git-bash, drive-paths) и разбивает сборку worker-образов на последовательные фазы, чтобы обойти гонку BuildKit при экспорте одного image-тега двумя targets.

### Диагностика

- `docker compose ps` — состояние сервисов;
- `docker compose logs -f gateway api worker-a worker-b` — логи (JWT/payload не логируются);
- `docker compose exec postgres psql -U postgres -d course -c "SELECT * FROM autocheck.processes"` — стабильные views (`flow_versions`, `processes`, `steps`, `jobs`, `attempts`, `signals`, `workflow_events`, `action_definitions`, `action_dispatches`);
- `./course.sh flow get <process-id>` — компактное состояние процесса;
- `curl http://localhost:8080/health/ready` — готовность; при недоступном PostgreSQL — 503.

### Ограничения

- Предметные payment maps и provider-simulator/Outbox/Inbox/HMAC не входят в неделю;
- Завершение manual шага через публичный action — неделя 3;
- Нет BPMN XML import/export, parallel/inclusive gateways, timers, cycles, subprocesses и compensation;
- Нет миграции запущенного process между версиями карты;
- Нет arbitrary expressions, code, URL или SQL из карты; нет специальных C#-веток по имени flow/step/action;
- `workflow_worker` не имеет прямого DML: все изменения workflow-состояния — через SECURITY DEFINER функции;
- `timeout_ms` ограничен 30000; request body — 64 KiB на gateway и api.

## Task

Задания недель: [task/week1](task/week1), [task/week2](task/week2), контракты: [08_program_and_contracts](task/week2/08_program_and_contracts/contracts/course-1).