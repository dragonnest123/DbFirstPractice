# NewProject — Week 3. Python-периметр и платёжные процессы

## Архитектура

Контур `compose.yaml` состоит из десяти сервисов:

```text
Client http://localhost:8080
  -> gateway (C# ASP.NET Core, whitelist-прокси, единственный опубликованный порт 8080)
  -> api      (C# action runtime + generic signature boundary, внутренний)
  -> postgres (PostgreSQL 16, база course, named volume pgdata; миграции встроены в image)
  -> worker-a / worker-b (общий C# image Workflow.Worker, lease owners worker-a/worker-b)
  -> outbox-dispatcher / receipt-adapter / inbox-reconciler (один Python 3.12 image, три entrypoints)
  -> provider-simulator (выданный Go image v0.2.0 по digest)
cli (C#) -> postgres (migration apply, action/flow publish/activate, flow start/get/signal)
```

Интеграционный периметр недели 3:

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

Используется выданный image `ghcr.io/fintech-dev-lab/internship-provider-simulator:v0.2.0` по закреплённому digest. `provider-simulator` принимает идемпотентный `POST /payments` и отправляет legacy callback без JWT/HMAC на `CALLBACK_URL` (capability передаётся через `PROVIDER_CALLBACK_CAPABILITY`). Подпись создаёт только Python-адаптер: compact sorted JSON, `X-Provider-Signature: v1=<lowercase hex>`, exact UTF-8 body bytes. Подробности статусов/error codes: `task/week3/docs/external-contracts.md`.

## Проверка

Открытый checker недели 3 запускается из отдельного клона пакета:

```bash
./check.sh --repo /path/to/participant-solution
```

или из этого репозитория:

```bash
./task/week3/check.sh --repo .
```

Checker собирает контур с `--pull --no-cache`, поднимает изолированный Compose project, прогоняет admission/startup/outbox/receipt/review/recovery/security и пишет `week-3-public-report.json` (без баллов и секретов). Коды завершения: `0` — все public checks пройдены, `1` — нарушен контракт решения, `2` — окружение/checker не готовы.

Локальные тесты:

```bash
dotnet test Api.Tests
dotnet test Api.IntegrationTests    # нужен Docker (Testcontainers PostgreSQL, миграции 001..016)
python -m pytest Python/tests
```

## Диагностика

- `docker compose ps` — состояние сервисов;
- `docker compose logs -f gateway api worker-a worker-b outbox-dispatcher receipt-adapter inbox-reconciler` — логи (секреты, JWT, подписи, полные тела и `message` не логируются);
- `docker compose exec postgres psql -U postgres -d course -c "SELECT * FROM autocheck.outbox"` — стабильные views недели 3: `external_requests`, `receipts`, `outbox`, `inbox`, `decisions` (плюс views недель 1–2);
- `./course.sh flow get <process-id>` — компактное состояние процесса.

## Ограничения

- Python не принимает предметных решений и не хранит авторитетное состояние; роли `outbox_dispatcher`/`inbox_reconciler` имеют EXECUTE ровно на закреплённые `delivery.*` функции и не имеют прямого DML.
- Provider-simulator — выданный компонент; его память не переживает recreate (такого требования к нему нет).
- Один messageId — одна дедупликационная область: duplicate возвращает исходный result, conflicting body даёт `409 idempotency.conflict`.
- Нет нескольких dispatcher/reconciler, jitter, финального dead-letter и failpoint-run — это неделя 4.
- C# API/worker images не пересобираются из-за имён flow/action; обе карты исполняются generic-ядром без специальных веток.
- `workflow_worker` не имеет прямого DML; request body — 64 KiB на gateway/api/adapter.

## Task

Задания недель: [task/week1](task/week1), [task/week2](task/week2), [task/week3](task/week3). Контракты недели 3: `task/week3/contracts/course-1`, полный контракт: `task/week3/docs/04-week-3.md`.