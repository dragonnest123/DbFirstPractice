# ADR-001: Trust boundary и происхождение context

Статус: принято.

## Контекст

`api.invoke(...)` выполняет предметную функцию с правами `SECURITY DEFINER`. Транзакция откатывает эффект при невалидном result, но только если policy проверена по доверенным данным. Нельзя допустить, чтобы клиент подменил `principal`, `consumer`, `scopes` или target через payload.

## Решение

1. `principal`, `consumer` и `scopes` берутся только из проверенного JWT (HS256, `iss`, `aud`, `exp`/`iat`, явные проверки JSON-типа каждого claim). Неверный тип claim -> `401 auth.invalid`, а не framework 500.
2. `correlationId` (UUID) и `deadline` генерирует runtime. `requestId` равен `Idempotency-Key` или `correlationId`.
3. C#-слой собирает `context` после валидации JWT и передаёт его в `api.invoke` отдельным аргументом; одноимённые поля payload на context не влияют (payload валидируется по `request_schema` с `additionalProperties: false`).
4. `api.invoke` повторно проверяет все scopes из `required_policy` по переданному context перед вызовом target.
5. `gateway` не имеет доступа к PostgreSQL и не хранит каталог; он форвардит контрактные заголовки без изменения смысла и не логирует JWT, credentials и полный payload.
6. Ошибки не раскрывают SQL, stack trace, connection string и внутренние targets: envelope с фиксированными `code`/`message`, `Include Error Detail=false`.

## Последствия

- Атака через payload-инъекцию target/schema невозможна: target резолвится только из `api.action_catalog`.
- Двухслойная проверка policy (HTTP + `api.invoke`) сохраняет защиту даже при прямом вызове функции из psql.