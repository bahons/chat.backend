# Codex B08 — Security/scale/release hardening (.NET)

Прочитай `docs/testing-and-release.md`, `docs/security.md`, `docs/decisions-and-risks.md`.

## Сделай

1. Проверь API + SignalR authorization matrix, контракты и все EF migrations/indexes; separate test DB, locking, outbox replay/idempotency.
2. Запусти `dotnet format`, `dotnet build -warnaserror`, `dotnet test`, актуализацию OpenAPI и integration tests с PostgreSQL; документируй FAIL/BLOCKER без скрытия.
3. Выполни role/verification abuse tests, secret/config checks, rate limits, file scanning/privacy audit, app logs without PII/SDP.
4. Нагрузочный smoke на REST + SignalR; real deployment strategy для scale-out/backplane и WebSocket proxy; health/metrics/alerts, backup/restore drills.
5. Проверь совместимость web (ASP.NET Core Blazor) и mobile (SignalR JS) с контрактами, endpoint versioning и безопасным logout.
6. Убедись, что нет billing/payment/paywall и запрещённого клиент-клиент общения.

## Результат

Таблица PASS/FAIL, выполненные команды, миграционные риски, blockers к production и корректный runbook. Никаких заявлений о готовности непроверенного WebRTC.
