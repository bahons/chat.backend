# Codex B07 — Операторы, assignment, аналитика [Этап 3]

Прочитай раздел Этапа 3 в API contract и `docs/security.md`. Требуется B06 либо соответствующие endpoint'ы этапов 1–2.

## Реализуй

1. EF Core tables/config/migrations для invitation, assignment и нужных событий аналитики без излишнего дублирования сообщений.
2. `GET /v1/team`, invite/accept/revoke/role update с membership ACL: OWNER/ADMIN управляют командой, AGENT нет; защитить последнего OWNER.
3. Inbox assignment для существующих B2C диалогов; atomic compare/update при гонках назначения и server-side события по SignalR.
4. Метрики ответа/обращений по серверным timestamps, эффективные PostgreSQL queries; без вымышленных показателей.
5. Только реально необходимые integration API hooks, без произвольных небезопасных webhooks.
6. Интеграционные тесты role revoke, cross-business, concurrent assignment; все бизнес-функции бесплатны.

## Критерии

История чата принадлежит компании, а не персональному оператору. Нет тарифных ACL.
