# Codex B04 — B2C диалоги и сообщения [Этап 1]

Прочитай `docs/data-model.md`, `docs/contracts/api-v1.md`, `docs/security.md`. Задача после B03.

## Реализуй

1. EF Core `Conversation` с unique composite `(clientUserId,businessId)`; `Message` и `MessageReadReceipt`, DB migrations, secure ownership checks. Создать диалог может только CLIENT с verified active business.
2. REST v1 conversations list/detail, messages cursor list/send, read marker; формат и ACL по контракту.
3. `clientMessageId` с уникальностью на стороне сервера, идемпотентность retry/concurrency; staff attribution только для internal audit; клиент видит компанию как отправителя.
4. Только TEXT сначала, подготовка metadata IMAGE/FILE (реальная загрузка на B05). Authorization policy checks на каждом handler; никакой отправки от бизнеса без существующего клиентского диалога.
5. Транзакционный Outbox для `message.created` и related updates: событие записано атомарно с сообщением, публикуется позже через worker.
6. Интеграционные тесты через PostgreSQL: race create, idempotent send, cross-client, cross-business, suspended/revoked membership, cursor pagination.

## Критерии

Нет личных/межбизнесовых диалогов, нет рассылки. Никакой guessed UUID не открывает чужую историю. EF/DTO/OpenAPI/CI обновлены.
