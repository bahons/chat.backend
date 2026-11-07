# Backend: модель данных v0.1

**Это логическая модель для EF Core migrations (Npgsql/PostgreSQL)**, не готовая схема. Идентификаторы UUID; времена timestamptz UTC; soft delete только там, где жизненный цикл и аудит этого требуют. Политика удаления и хранения — отдельное решение до production.

| Сущность | Ключевые поля | Ограничения |
|---|---|---|
| `User` | `id`, `userKind`, `email`, `passwordHash`, `emailVerifiedAt?`, `displayName`, `status`, timestamps | normalized unique email; hash Argon2id/современный аналог; no plaintext |
| `RefreshSession` | `id`, `userId`, `refreshTokenHash`, `deviceLabel?`, `expiresAt`, `revokedAt?` | revoked on logout/rotation |
| `Business` | `id`, `legalName`, `displayName`, `registrationNumber`, `country`, `category`, `description?`, `verificationStatus`, `isActive`, timestamps | searchable iff VERIFIED + ACTIVE |
| `BusinessMembership` | `businessId`, `userId`, `role`, `status`, timestamps | unique `(businessId,userId)`; staff only; MVP at least OWNER |
| `BusinessApplication` | `id`, `businessId`, `submittedAt`, `reviewedAt?`, `reviewedBy?`, `decisionReason?` | immutable decision audit history |
| `VerificationDocument` | `id`, `applicationId`, `privateObjectKey`, `mimeType`, `size`, `scanStatus` | NEVER public URL; restricted role access |
| `Conversation` | `id`, `clientUserId`, `businessId`, `createdAt`, `updatedAt`, `lastMessageAt?` | **unique** `(clientUserId,businessId)`; client userKind only |
| `Message` | `id`, `conversationId`, `authorUserId`, `authorSide`, `clientMessageId`, `kind`, `text?`, timestamps | author is client or current staff member; unique per author/conversation `clientMessageId` |
| `Attachment` | `id`, `uploaderUserId`, `objectKey`, `kind`, `mimeType`, `size`, `status`, `checksum?` | cannot attach before VERIFIED/READY state; bound to authorized context |
| `MessageAttachment` | `messageId`, `attachmentId` | ownership + matching conversation authorization |
| `ConversationReadState` | `conversationId`, `side`, `lastReadMessageId`, `updatedAt` | side CLIENT or BUSINESS; one per side |
| `DeviceRegistration` | `id`, `userId`, `platform`, `pushToken`, timestamps | sensitive, revoke on logout |
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
