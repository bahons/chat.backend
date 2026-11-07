# Codex B05 — ASP.NET Core SignalR, вложения, push [завершение Этапа 1]

Прочитай `docs/contracts/events-v1.md`, `docs/contracts/api-v1.md`, `docs/security.md`, `docs/architecture.md`. Требуется B04.

## Реализуй

1. **ASP.NET Core SignalR** `MessageHub` на `/hubs/messaging`; auth на connect и для `JoinConversation`, `LeaveConversation`, `SetTyping`, `MarkRead` (все Hub методы с ACL). Группы `conversation:{uuid}`. Имена server/client methods — строго в SignalR contract.
2. Отправка сообщений остаётся **только REST**. Worker публикует SignalR события из Outbox **после commit**; reconnect через REST, dedup по message.id/clientMessageId.
3. S3-compatible private files, `POST /v1/uploads/presign`, `/complete`, проверка mime/size, scan/quarantine, ownership/access permissions, short-lived downloads. Не путать с private verification docs.
4. Push registration/revoke API и worker, безопасные payloads (без текста/секретов), retry policy. Документируй новые endpoints в OpenAPI до consumer интеграции.
5. Revoke membership/Business suspend: активные SignalR connections не должны сохранять права. Переоценка ACL на каждом методе и при адресации отправки.
6. Unit/integration tests: foreign group, stale role, revoked operator, upload ACL, retry, SignalR reconnect, delivery after transaction. Проверка совместимости с .NET/JS SignalR clients.

## Критерии

Сотрудник VERIFIED компании видит и отвечает через web и mobile; после reconnect история целая, дублей нет; все сообщения бесплатны. Никакого Socket.IO в коде или документации.
