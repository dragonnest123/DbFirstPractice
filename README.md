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

`gateway` обращается к `api` по Compose DNS (`http://api:8080`), не через `localhost`. `cli` подключается к `postgres` под ролью `course_migration`. Реальные секреты в репозиторий не помещаются.

### Миграции

`Api/Migrations/001..005` применяются автоматически при первом старте `postgres` через `/docker-entrypoint-initdb.d` (владелец — суперпользователь, объекты передаются `course_owner`). Внешние миграции применяются `cli migration apply <dir>`: лексикографический порядок, одна транзакция на файл, sha256-checksum в `public.schema_migrations`, изменение применённого файла отклоняется (`manifest.conflict`). API и worker не используют migration credentials.

### Проверка

```bash
./check.sh
```

Открытая проверка собирает контур, публикует fixture-actions (`opencheck.probe`), гоняет матрицу безопасности/контрактов/идемпотентности и пишет `week-1-public-report.json` в корень. Также доступны smoke-тесты CLI:

```bash
./course.sh action validate /autocheck/input/manifests/opencheck-probe-v1.action.json
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
- `timeout_ms` ограничен 30000 (`task/contracts/course-1/action-manifest.schema.json`), gateway-таймаут — 75 с.

## Task

Исходное задание недели: [task/README.md](task/README.md), контракты: [task/docs/contract-reference.md](task/docs/contract-reference.md), machine schemas: [task/contracts/course-1](task/contracts/course-1).
