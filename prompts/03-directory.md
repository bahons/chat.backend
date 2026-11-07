# Codex B03 — Каталог VERIFIED компаний [Этап 1]

Прочитай contracts, security и созданные на B02 EF entities.

## Реализуй

1. `GET /v1/businesses?q=&cursor=&limit=` и `GET /v1/businesses/{id}` для CLIENT; только `VERIFIED && isActive`.
2. EF Core efficient `AsNoTracking` projections на публичный DTO, без owner email/registration secrets/documents.
3. Индексы PostgreSQL + stable cursor pagination; ограничить поиск от abuse, проверить эффективность запросов и параметризацию.
4. Pending, Rejected, Suspended скрыты и по прямому UUID возвращают безопасный 404.
5. xUnit WebApplicationFactory integration + contract tests, OpenAPI update.

## Критерии

В каталоге нет людей/персональных контактов. Все функции бесплатны.
