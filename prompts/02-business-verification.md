# Codex B02 — Бизнес-заявки через веб и верификация [Этап 1]

Прочитай `docs/security.md`, `docs/data-model.md`, `docs/contracts/api-v1.md`, B01. Mobile UI не регистрирует компании. Не делать "web-only auth" по Origin/User-Agent.

## Реализуй

1. EF Core entities/migrations/configurations: `Business`, `BusinessApplication`, `VerificationDocument`, `AuditLog`, `BusinessMembership`. Обязательные unique/foreign keys и DB transaction для owner+application.
2. `POST /v1/business-applications` с валидацией, rate limits, safe private verification file upload, `PENDING_VERIFICATION`. При необходимости добавь guest draft token flow в OpenAPI и contract docs **до** consumer integration.
3. `GET/PATCH /v1/my-business` для owner/admin, ограничь редактируемые поля, до approval запрети staff business API.
4. `GET /v1/admin/business-applications`, `/.../{id}`, `POST /.../{id}/decision`: только PLATFORM_ADMIN, approve/reject with reason, audit trail, validated state transitions.
5. Approve → VERIFIED + staff access web/mobile; reject/suspend disables business operations and public presence; проверить race conditions и email conflict/reapplication.
6. Negative tests: чужие private docs, non-admin approve, repeated approval, business activity before verification; EF migration up/down test where safe.

## Критерии

Ручная проверка обязательна. Владелец Pending видит статус в своём веб-кабинете, но не доступен для business messaging. Документы не опубликованы публично. OpenAPI и тесты обновлены.
