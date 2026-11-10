# Backend: модель данных B01–B05

Логическая модель для EF Core migrations (Npgsql/PostgreSQL). Идентификаторы UUID, времена timestamptz UTC. Реализованные ограничения и политика B05.4 приведены ниже.

| Сущность | Ключевые поля | Ограничения |
|---|---|---|
| `User` | `id`, `userKind`, `email`, `passwordHash`, `emailVerifiedAt?`, `displayName`, `status`, timestamps | normalized unique email; hash Argon2id/современный аналог; no plaintext |
| `RefreshSession` | `id`, `userId`, `refreshTokenHash`, `deviceLabel?`, `expiresAt`, `revokedAt?` | revoked on logout/rotation |
| `Business` | `id`, `legalName`, `displayName`, `registrationNumber`, `country`, `category`, `description?`, `verificationStatus`, `isActive`, timestamps | searchable iff VERIFIED + ACTIVE |
| `BusinessMembership` | `businessId`, `userId`, `role`, `status`, timestamps | unique `(businessId,userId)`; staff only; MVP at least OWNER |
| `BusinessApplication` | `id`, `businessId`, `submittedAt`, `reviewedAt?`, `reviewedBy?`, `decisionReason?` | Одна открытая заявка на бизнес, прежние решения сохраняются |
| `VerificationDocument` | `id`, `draftId`, `applicationId?`, `Content`, `mimeType`, `size`, `scanStatus` | Приватные байты в PostgreSQL, только READY через админский endpoint |
| `Conversation` | `id`, `clientUserId`, `businessId`, `createdAt`, `updatedAt`, `lastMessageAt?` | **unique** `(clientUserId,businessId)`; client userKind only |
| `Message` | `id`, `conversationId`, `authorUserId`, `authorSide`, `clientMessageId`, `kind`, `text?`, timestamps | author is client or current staff member; unique per author/conversation `clientMessageId` |
| `Attachment` | `id`, `uploaderUserId`, `objectKey`, `kind`, `mimeType`, `size`, `status`, `checksum?` | cannot attach before VERIFIED/READY state; bound to authorized context |
| `Attachment.MessageId` | `conversationId`, `messageId?` | Составной FK на сообщение в том же диалоге |
| `MessageReadReceipt` | `conversationId`, `side`, `lastMessageId`, `updatedAt` | Составной FK на сообщение и одна отметка на сторону |
| `DeviceRegistration` | `id`, `userId`, `familyId`, `pushToken`, `revokedAt?`, `releasedAt?` | Уникальный Expo-токен, передача только после release владельцем |
| `AuditLog` | `id`, `actorId`, `action`, `entityType`, `entityId`, `createdAt`, `metadata` | no raw documents or message text |

Этап 2: `Call(id,conversationId,initiatorUserId,acceptedByUserId?,kind,state,startedAt?,endedAt?,reason?)`.  
Этап 3: `BusinessInvitation`, `ConversationAssignment`, aggregate analytics.

## Важные database invariants

1. Unique conversation per (client,business), created transactionally; use DB uniqueness and conflict-safe upsert.
2. Сообщение подписывает **фактический** автор, а `authorSide` выводится сервером из authenticated principal + membership, не берётся из клиента.
3. Нельзя после suspension отправить/получить новое содержимое через старый токен; security checks читать актуальный status.
4. Idempotency предотвращает дубли при retries через HTTP, offline queue и reconnect.
5. Только проверенные staff участники имеют доступ к `Business` conversation; у старых/отключённых сотрудников доступ отзывается.
6. `MessageAttachment` и `Attachment` не должны ссылаться на произвольный файл других пользователей/диалогов.
7. Индексы: `(businessId,lastMessageAt)`, `(clientUserId,lastMessageAt)`, `(conversationId,createdAt,id)`, business verification + searchable name. Полнотекстовый поиск можно добавлять по мере необходимости.

Составной FK `Conversations(ClientUserId, ClientKind) → Users(Id, Kind)` и CHECK фиксируют
`CLIENT`. Составные FK вложения и отметки ссылаются на `Messages(ConversationId, Id)`.
Альтернативные ключи и индексы добавляют стоимость записи, но защищают от SQL и импортов.
Права автора и memberships проверяются приложением в момент операции. Partial unique index на
`Businesses(Country, RegistrationNumber)` покрывает `VERIFIED` и `SUSPENDED`. Inbox v2 индексируется
по `(clientUserId,createdAt,id)` и `(businessId,createdAt,id)`.

## Retention B05.4

Это технические исходные сроки. Утверждение production-политики, её юрисдикция и исключения
относятся к владельцу данных. Для Production по умолчанию scheduled deletion выключен.
Для включения требуется согласованная настройка `Retention__Approved=true`. Общий
`Retention__LegalHold=true` приостанавливает scheduled DB/S3 cleanup. Для внешних lifecycle rules
и backup retention требуется отдельное управление. Настройка hold должна применяться и там.

| Данные | Технический срок и правило |
| --- | --- |
| Refresh, включая использованные и отозванные токены | `Retention__ExpiredSessionsDays=7` после `ExpiresAt`. Пока семейство может жить, старые хеши сохраняются для replay detection. |
| Черновики | Просроченные незавершённые до 200 за запуск. Строки с доказательствами поданной заявки исключены. Временные черновики в Development убираются без production approval. |
| Поданные документы, решения, audit | Scheduled удаления нет. Ограничить доступ, сохранять доказательства до утверждённой политики и окончания hold. |
| Завершённые Outbox, push и SMTP | `Retention__CompletedWorkDays=7` после завершения. Pending или leased записи сохраняются. Mail ciphertext очищается при terminal outcome. |
| Текст и вложения сообщений | Scheduled удаления канонических данных нет. Outbox хранит ссылку на сообщение. Старые копии сокращены миграцией, backups очищаются по отдельному сроку. |
| Staging, карантин и непривязанные медиа | `Retention__UnattachedMediaHours=24` после PUT deadline. Сначала транзакционный tombstone, потом идемпотентный S3 delete. Для прикреплённых READY удаляется только staging. Метаданные остаются для сверки. |
| Expo-регистрация | 90 дней без обновления или недействительное семейство прекращают доставку. Старая привязка хранится как защита от перехвата до явного release. |

DB retention worker запускается каждые `Retention__SweepIntervalSeconds=30` секунд и чистит
партиями до 200 записей каждого типа. Проход ограничен `Retention__MaxBatchesPerSweep=50`
и мягким бюджетом `Retention__SweepBudgetSeconds=5`: начатая партия завершается, новая
не начинается после истечения бюджета. Approval и legal hold перечитываются перед каждой
партией. При ошибке следующий проход продолжает обработку. Очистка черновиков сохраняет
отдельный интервал 10 минут и лимит 200. S3 worker повторяет по `CleanupAfter`, а lag и завершение отслеживаются
по событиям `231`–`234` и `236`–`237`. Для сверки считайте устаревшие записи с
`ObjectsDeletedAt IS NULL` и отдельно сверяйте prefix inventory с метаданными без автоматического
удаления несовпадений. Перед restore остановите воркеры и проверьте согласованность версий БД,
объектов и ключей. Hold вступает в силу для следующих партий. Для срочного hold остановите cleanup.
