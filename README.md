# NewProject — Week 4. Надёжность, экспоненциальный backoff и диагностика

## Архитектура

Контур `compose.yaml` состоит из двенадцати сервисов:

```text
Client http://localhost:8080
  -> gateway (C# ASP.NET Core, whitelist-прокси, единственный опубликованный порт 8080)
  -> api      (C# action runtime + generic signature boundary, внутренний, :8080 metrics)
  -> postgres (PostgreSQL 16, база course, named volume pgdata; миграции встроены в image)
  -> worker-a / worker-b (общий C# image Workflow.Worker, lease owners worker-a/worker-b, :8080)
  -> outbox-dispatcher / outbox-dispatcher-b (общий Python image, lease owners)
  -> receipt-adapter (Python, :8082)
  -> inbox-reconciler / inbox-reconciler-b (общий Python image, :8080)
  -> provider-simulator (выданный Go image v0.2.0 по digest)
cli (C#) -> postgres (migration apply, delivery policy, action/flow publish/activate, start/get/signal)
```

Интеграционный периметр недели 4 (две реплики dispatcher/reconciler, общий lease на строке):

```text
Outbox -> Python outbox-dispatcher -> provider v0.2.0
provider legacy callback -> Python receipt-adapter -> gateway -> generic C# API -> receipt.accept -> Inbox
Inbox -> Python inbox-reconciler -> workflow signal -> generic C# worker -> final action
Outbox/Job -> lease + retry-policy в PostgreSQL -> diagnostics.trace -> autocheck views
```

Интеграционный периметр (недели 3–4):

```text
Outbox -> Python outbox-dispatcher -> provider v0.2.0
provider legacy callback -> Python receipt-adapter -> gateway -> generic C# API -> receipt.accept -> Inbox
Inbox -> Python inbox-reconciler -> workflow signal -> generic C# worker -> final action
```

Единственная точка предметного выполнения — PostgreSQL-функция `api.invoke(...)`. C#-слой `api` выполняет JWT-аутентификацию, проверку `X-Provider-Signature` (HMAC-SHA256 по exact body bytes, constant-time) и кладёт в trusted-контекст только `transport.signatureVerified`/`transport.signatureVersion`/`transport.bodySha256`, затем резолвит immutable каталог `api.action_catalog`, валидирует request/response схемы и управляет одной транзакцией вокруг `api.invoke`. Python не выбирает flow, лимит, переход или финальный статус и не хранит авторитетное состояние. Подробная схема: [C4](docs/c4.puml), решения: [ADR-001](docs/adr-001-trust-boundary.md), [ADR-002](docs/adr-002-technical-vs-domain-result.md), [ADR-003](docs/adr-003-lease-fencing.md), [ADR-004](docs/adr-004-trust-boundary-acl.md).

Два платёжных процесса исполняются общим workflow-ядром без веток по именам flow/action:

- `payment-processing`: `validate -> prepare_external -> wait_receipt -> apply_receipt -> complete|reject -> end`;
- `payment-review`: `validate -> check_limit -> [WITHIN_LIMIT] approve -> end | [REVIEW_REQUIRED] manual -> approve|reject -> end`.

Server-side binding: `PAYMENT_EXECUTION -> payment-processing`, `PAYMENT_APPROVAL -> payment-review`. Лимит `course-limit-v1`: до `100000.00 RUB` включительно — auto approve, выше — manual.

## Запуск

Prerequisites: Docker с Docker Compose v2 (`!override`/`!reset`, `config --no-env-resolution`), Python 3.11+ для проверки.

Переменные (без `COURSE_*`/`PROVIDER_*`-значений контур не стартует корректно; для локального запуска задайте их в окружении или `.env`, не коммитьте реальные секреты):

```bash
export COURSE_JWT_ISSUER=moduledev-course
export COURSE_JWT_AUDIENCE=moduledev-api
export COURSE_JWT_SIGNING_KEY=<ключ HS256>
export PROVIDER_CALLBACK_CAPABILITY=<непредсказуемый сегмент>
export PROVIDER_CALLBACK_TOKEN=<JWT principal receipt-provider, scope receipt:write>
export PROVIDER_HMAC_SECRET=<ключ HMAC>
docker compose up -d --build
```

После старта без ручных SQL-команд доступны:

- `POST http://localhost:8080/api/payment/request` — создать операцию (JWT + Idempotency-Key);
- `POST http://localhost:8080/api/payment/submit` — привязать operation к flow по server-side binding;
- `POST http://localhost:8080/api/operation/events` — события операции;
- `POST http://localhost:8080/api/workflow/manual` — ручное решение (policy `workflow:manual`);
- `POST http://localhost:8080/api/receipt/accept` — подписанная квитанция (policy `receipt:write`);
- `GET http://localhost:8080/health/live`, `/health/ready`, `/openapi/default.json`.

## Python-периметр

Один локально собранный image `newproject-python:local` (`Python/Dockerfile`, Python 3.12) с тремя entrypoints:

| Процесс | Вход | Выход | Не делает |
|---|---|---|---|
| `outbox-dispatcher` | `delivery.claim_outbox(owner, limit)` | `POST {PROVIDER_URL}/payments` с `Idempotency-Key=externalRequestId`/`X-Correlation-ID`, затем `succeed/fail_outbox` | не меняет operation/process, не выбирает retry-политику |
| `receipt-adapter` | legacy callback `/callbacks/provider-v02/{capability}` | signed receipt v1 → `POST /api/receipt/accept` через gateway | не подключается к PostgreSQL, не хранит состояние |
| `inbox-reconciler` | `delivery.reconcile_inbox(limit)` | workflow signal (через `workflow.accept_signal`) | не читает/не меняет таблицы, не исполняет flow |

Службы не публикуют host-портов. Python-код и unit-тесты: `Python/app`, `Python/tests`.

## Provider

Используется выданный image `ghcr.io/fintech-dev-lab/internship-provider-simulator:v0.2.0` по закреплённому digest. `provider-simulator` принимает идемпотентный `POST /payments` и отправляет legacy callback без JWT/HMAC на `CALLBACK_URL` (capability передаётся через `PROVIDER_CALLBACK_CAPABILITY`). Подпись создаёт только Python-адаптер: compact sorted JSON, `X-Provider-Signature: v1=<lowercase hex>`, exact UTF-8 body bytes. Подробности статусов/error codes: `task/week4/docs/external-contracts.md`.

## Надёжность недели 4

Retry-политика живёт в PostgreSQL и применяется функцией `delivery.apply_outbox_policy(...)`, которую CLI вызывает при старте без аргументов (идемпотентный upsert). Значения по умолчанию: `COURSE_OUTBOX_MAX_ATTEMPTS=4`, `COURSE_OUTBOX_BACKOFF_BASE_MS=200`, `COURSE_OUTBOX_BACKOFF_MAX_MS=800`, `COURSE_OUTBOX_JITTER_MAX_MS=100`, `COURSE_OUTBOX_LEASE_MS=2000`, `COURSE_JOB_LEASE_MS=2000`.

- `delivery.claim_outbox(owner, limit)` выдаёт строку только с `state='PENDING'`, `next_attempt_at <= now()`, протухшим `lease_until`; `lease_owner`/`lease_version`/`attempt_count` инкрементируются в той же транзакции.
- `next_attempt_at` считает PostgreSQL: `backoff_base * 2^(attempt-1)`, ограниченный `backoff_max`, плюс `jitter` из `[0, jitter_max]`. Клиент не решает, когда повторять.
- Классификация: transport/timeout и HTTP `408/429/5xx` — retryable; остальные `4xx` — терминальные. Четвёртая неудача переводит строку в `DEAD` с `last_error_code`.
- Lease-фенсинг одинаков для job и outbox: `finish_job`/`fail_job`/`succeed_outbox`/`fail_outbox` проверяют `lease_version` и отклоняют чужого владельца (`workflow.lease_stale`). Проигравший реплике worker не пишет ничего, включая маркер отказа, и только увеличивает счётчик конфликтов.
- Failpoint-границы (только при `COURSE_TEST_PROFILE=1`): `after_job_claim`, `after_action_before_finish`, `after_outbox_claim`, `after_provider_response`, `after_inbox_saved`, `after_manual_decision`. Достижение границы печатает ровно `{"event":"failpoint.reached","name":"...","instanceId":"..."}` и блокирует процесс.
- `diagnostics.stalled_v1` отдаёт операции, у которых доставка исчерпана и которые ждут квитанцию дольше порога. Правило: строка outbox в `state='DEAD'` (все автоматические попытки использованы), шаг `WAIT_SIGNAL` процесса всё ещё в `state='WAITING'`, и с сохранённого `delivery.outbox.dead_at` прошло не меньше `10 секунд`. Возраст считает только SQL от сохранённого факта, поэтому состав выборки не зависит от того, что успел увидеть вызывающий; чтение ничего не меняет и не создаёт повторной доставки. Операция уходит из выборки, как только квитанция применена: шаг перестаёт быть `WAITING`. Отдельный порог именно потому, что свежий `DEAD` — это нормальное завершение попыток, а не зависание.
- Подключение к PostgreSQL из CLI повторяет только транспортные отказы (`Shared/Services/PostgresConnect.cs`): ограниченный бюджет попыток, без ретраев SQL-ошибок. Ошибка миграции или запроса доходит до вызывающего с первой попытки.
- Healthcheck `postgres` в `compose.yaml` намеренно идёт по TCP (`pg_isready -h 127.0.0.1`): временный сервер инициализации слушает только Unix-сокет, и проверка по сокету объявляла бы базу готовой до того, как она начнёт принимать подключения.
- Диагностика: `diagnostics.trace_v1` собирает полный след по одному идентификатору (operation/process/step/job/attempt/decision/correlation/request/externalRequest/message) обходом графа до неподвижной точки; ответ отдаёт `outcome=FOUND` с `dispatches`, `operation`, `process`, `steps`, `jobs`, `attempts`, `outbox`, `inbox`, `receipts`, `decisions`, `operationEvents`. Всё состояние читается из 17 стабильных views `autocheck` (роль `autocheck_reader`: `LOGIN NOINHERIT`, только SELECT, безEXECUTE на прикладных функциях).
- Метрики: шесть бизнес-серий публикует только API (`workflow_jobs_ready`, `workflow_job_oldest_age_seconds`, `workflow_processes_waiting`, `outbox_pending`, `outbox_oldest_age_seconds`, `workflow_failures`); worker и Python добавляют свои process-local серии (`workflow_worker_*`, `outbox_dispatcher_*`, `inbox_reconciler_*`). Метки ограничены фиксированным набором значений.

## Проверка

Открытый checker недели 4 запускается из отдельного клона пакета:

```bash
./check.sh --repo /path/to/participant-solution
```

или из этого репозитория (обе команды равнозначны):

```bash
./check.sh --repo .
./task/week4/check.sh --repo .
```

Checker собирает контур с `--pull --no-cache`, поднимает изолированный Compose project, прогоняет admission/startup/observability/delivery/receipt/review/recovery/security и пишет `week-4-public-report.json` (без баллов и секретов). Коды завершения: `0` — все public checks пройдены, `1` — нарушен контракт решения, `2` — окружение/checker не готовы. `EnvironmentFailure` (`2`) означает, что хост не успел за окном восстановления, и требует повторного прогона, а не правки кода.

Локальные тесты:

```bash
dotnet test Api.Tests
dotnet test Api.IntegrationTests    # нужен Docker (Testcontainers PostgreSQL, миграции 001..017)
python -m pytest Python/tests
```

## Диагностика

- `docker compose ps` — состояние сервисов;
- `docker compose logs -f gateway api worker-a worker-b outbox-dispatcher outbox-dispatcher-b receipt-adapter inbox-reconciler inbox-reconciler-b` — логи (секреты, JWT, подписи, полные тела и `message` не логируются);
- `curl -s localhost:8080/health/ready`, `curl -s <service>:8080/metrics` внутри контура — живость, готовность и метрики;
- `docker compose exec postgres psql -U postgres -d course -c "SELECT * FROM autocheck.outbox"` — стабильные views недели 4: `outbox`, `jobs`, `attempts`, `decisions`, `action_dispatches`, `signals`, `workflow_events` (плюс views недель 1–3);
- `./course.sh flow get <process-id>` — компактное состояние процесса.

Runbook по зависшим ожиданиям квитанции:

```bash
TOKEN=<jwt>   # курсор выдаётся отдельно, в репозитории его нет

# 1. Полный след по операции из выборки stalled.
curl -s -X POST localhost:8080/api/diagnostics/trace \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' \
  -d '{"identifier":"<operation-id>"}'

# 2. Сама выборка: операции с исчерпанной доставкой, ждущие квитанцию дольше 10 секунд.
curl -s -X POST localhost:8080/api/diagnostics/stalled \
  -H "Authorization: Bearer $TOKEN" -H 'Content-Type: application/json' -d '{}'

# 3. Состояние доставки по операции, чтобы отличить исчерпанные попытки от активной доставки.
docker compose exec postgres psql -U postgres -d course -c \
  "SELECT external_request_id, state, attempts, last_error_code, dead_at, next_attempt_at
     FROM delivery.outbox ORDER BY created_at DESC LIMIT 20"
```

Операция вне выборки, хотя `state='DEAD'`, — это нормально: порог ещё не прошёл. Если `dead_at` старше порога, а операции в выборке нет, проверьте шаг `WAIT_SIGNAL`: квитанция уже применена, и бизнес-результат смотрите в `diagnostics.trace`.

## Ограничения

- Python не принимает предметных решений и не хранит авторитетное состояние; роли `outbox_dispatcher`/`inbox_reconciler` имеют EXECUTE ровно на закреплённые `delivery.*` функции и не имеют прямого DML.
- Provider-simulator — выданный компонент; его память не переживает recreate (такого требования к нему нет).
- Один messageId — одна дедупликационная область: duplicate возвращает исходный result, conflicting body даёт `409 idempotency.conflict`.
- Jitter делает расписание retry недетерминированным; окно восстановления в checker рассчитано на быстрый хост.
- C# API/worker images не пересобираются из-за имён flow/action; обе карты исполняются generic-ядром без специальных веток.
- `workflow_worker` не имеет прямого DML; request body — 64 KiB на gateway/api/adapter.

## Task

Задания недель: [task/week1](task/week1), [task/week2](task/week2), [task/week3](task/week3), [task/week4](task/week4). Контракты недели 4: `task/week4/contracts/course-1`, полный контракт: `task/week4/docs/05-week-4.md`, наблюдаемость: `task/week4/docs/observability-contracts.md`.