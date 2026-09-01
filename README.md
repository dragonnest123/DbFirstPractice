# NewProject — Week 1. Database-first action runtime

## Решение

### Архитектура

Контур состоит из четырёх сервисов `compose.yaml`:

```text
Client http://localhost:8080
  -> gateway (C# ASP.NET Core, whitelist-прокси, единственный опубликованный порт 8080)
  -> api      (C# action runtime, внутренний, без опубликованных портов)
  -> postgres (PostgreSQL, база course, named volume pgdata)
cli (C#) -> postgres (migration apply, action publish/activate/disable)
```

Единственная точка предметного выполнения — PostgreSQL-функция `api.invoke(...)` (SECURITY DEFINER, владелец `course_owner` с NOLOGIN). C#-слой `api` выполняет JWT-аутентификацию, резолв action из immutable каталога `api.action_catalog`, валидацию request/response схем и управляет одной транзакцией вокруг `api.invoke`. Новые actions регистрируются манифестом через `cli` без пересборки `gateway`/`api`. Подробная схема контейнеров: [C4](docs/c4.puml). Решения по границам доверия: [ADR-001](docs/adr-001-trust-boundary.md), [ADR-002](docs/adr-002-technical-vs-domain-result.md).

### Запуск

Prerequisites: Docker с Docker Compose v2, Python 3 для открытой проверки.

```bash
docker compose up -d --build
```

После старта без ручных SQL-команд доступны:

- `POST http://localhost:8080/api/payment/request` — создать операцию (JWT + Idempotency-Key);
- `POST http://localhost:8080/api/operation/get` — прочитать операцию;
- `GET http://localhost:8080/health/live` — liveness процесса;
- `GET http://localhost:8080/health/ready` — готовность контура (PostgreSQL + инициализация);
- `GET http://localhost:8080/openapi/default.json` — OpenAPI включённых default-маршрутов.

### Конфигурация

Сервис `api` читает переменные окружения:

| Переменная | Значение |
|---|---|
| `COURSE_JWT_ISSUER` | `moduledev-course` |
| `COURSE_JWT_AUDIENCE` | `moduledev-api` |
| `COURSE_JWT_SIGNING_KEY` | HS256-ключ, не менее 32 байт (задаётся через `${VAR:-default}`; проверка подменяет через Compose override) |
| `POSTGRES_CONNECTION` | строка подключения роли `course_runtime` |

Сервис `cli` использует две роли: `course_migration` (`POSTGRES_CONNECTION`) — только для `migration apply` (checksummed SQL, DDL, `public.schema_migrations`); `course_publication` (`PUBLICATION_CONNECTION`) — для `action publish/activate/disable/list`, у которой нет прямого DML: изменения каталога выполняются только атомарными функциями `api.publish_action`/`api.activate_action`/`api.disable_action` (SECURITY DEFINER, владелец `course_owner` NOLOGIN). Ни одна из ролей CLI не имеет записи в `payment.operations`, `payment.operation_events` и `api.action_dispatches`. Реальные секреты в репозиторий не помещаются.

### Миграции

`Api/Migrations/001..008` применяются автоматически при первом старте `postgres` через `/docker-entrypoint-initdb.d` (владелец — суперпользователь, объекты передаются `course_owner`). Внешние миграции применяются `cli migration apply <dir>`: лексикографический порядок, одна транзакция на файл, sha256-checksum в `public.schema_migrations`, изменение применённого файла отклоняется (`manifest.conflict`). В той же транзакции выполняется финализация: все publishable target-функции `(jsonb,jsonb)→jsonb` получают владельца `course_target` (NOLOGIN NOSUPERUSER), а их схемам/таблицам выдаются минимальные grants. `api.publish_action` проверяет перед публикацией, что target существует с точной сигнатурой и принадлежит `course_target`. Роль `course_migration` сужена до DDL и `public.schema_migrations`; публикация actions идёт только через атомарные catalog-функции (`006_publication_api.sql`), неизменяемость опубликованной версии защищена PostgreSQL trigger-ами (`007_manifest_immutability.sql`). Предметная история защищена на уровне DB (`008_operation_immutability.sql`): `payment.operation_events` — append-only (UPDATE/DELETE запрещены), identity/payload-поля `payment.operations` неизменяемы после создания (допустимы только status/process_id/updated_at); direct UPDATE/DELETE исключены для runtime/publication/migration ролей. API и worker не используют migration credentials.

### Проверка

```bash
./check.sh
```

Открытая проверка собирает контур, публикует fixture-actions (`opencheck.probe`), гоняет матрицу безопасности/контрактов/идемпотентности и пишет `week-1-public-report.json` в корень. Также доступны smoke-тесты CLI:

```bash
./course.sh action validate /autocheck/input/manifests/opencheck-probe-v1.action.json
```

Собственные regression tests (нужен запущенный Docker):

```bash
dotnet test Api.Tests          # unit: error mapping, envelope, HTTP statuses
dotnet test Api.IntegrationTests # integration: publication conflicts, privileges, append-only history, domain errors через HTTP (Testcontainers PostgreSQL + миграции 001..008)
```

### Диагностика

- `docker compose ps` — состояние сервисов;
- `docker compose logs -f gateway api` — логи (JWT/payload не логируются);
- `docker compose exec postgres psql -U postgres -d course -c "SELECT * FROM autocheck.contract_info"` — проверочные проекции;
- `curl http://localhost:8080/health/ready` — готовность; при недоступном PostgreSQL — 503;
- `curl http://localhost:8080/openapi/default.json` — manifest-driven OpenAPI.

### Ограничения

- Идемпотентность `payment.request` — в PostgreSQL (`api.idempotency_store`, уникальность `scope_key+request_id`), без process-local lock;
- Транзакция одна: `error`/неизвестный outcome/невалидный result откатывают весь предметный эффект;
- В неделю 1 не входят workflow, outbox, retries и worker;
- `timeout_ms` ограничен 30000 (`task/contracts/course-1/action-manifest.schema.json`), gateway-таймаут — 75 с;
- Request body ограничен 64 KiB на gateway и api (Kestrel `MaxRequestBodySize`, превышение — 413 до чтения); api проверяет JWT до материализации body, неаутентифицированные запросы не читают payload;
- Timeout и отмена клиента (`RequestAborted`) линкуются в единый токен выполнения; rollback выполняется независимым bounded-токеном (5 с), timeout отображается как `504 action.timeout` на обеих границах (api и gateway).

## Task

Исходное задание недели: [task/README.md](task/README.md), контракты: [task/docs/contract-reference.md](task/docs/contract-reference.md), machine schemas: [task/contracts/course-1](task/contracts/course-1).
