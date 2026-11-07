# Realtime contract — ASP.NET Core SignalR (v0.2.0)

**Статус:** baseline, одинаковый в backend/frontend/mobile. Это проектный контракт, не действующий API до выполнения B05/B06.

**Технология:** **ASP.NET Core SignalR**, Hub `https://<api-host>/hubs/messaging`, TLS/WSS, SignalR protocol JSON. **Это не Socket.IO и не raw WebSocket**. Browser/server .NET клиент: `Microsoft.AspNetCore.SignalR.Client`; React Native клиент: официальный `@microsoft/signalr` после проверки RN/transport compatibility. Имена Hub methods и клиентских событий ниже чувствительны к контракту и тестируются кросс-клиентски.

## Авторизация, соединения и доставка

- AuthN JWT/cookie according to client. На backend верифицировать user, membership, business verification, revocation. `conversationId` не даёт права доступа.
- Web Blazor Server: SignalR `.NET` client из server-side scoped session к backend, user-scoped токен **не отдавать в browser JS**. Держать ограниченное число connections и освобождать по circuit/session lifecycle.
- React Native: `withUrl(hubUrl, { accessTokenFactory })`; проверить поведение токенов конкретного SignalR transport. Если access token попадает в query string во время WebSocket/SSE handshake, принимать **только на Hub path**, требовать WSS, short-lived token, **не логировать query string**; не вставлять токены вручную в URL.
- После disconnect/reconnect повторно `JoinConversation` для открытых чатов, догрузка истории REST cursor; Hub не источник истины, idempotency на REST.
- `JoinConversation` server validates per membership/role и разрешает группу `conversation:{uuid}`; ACL повторяется на каждом методе. После revoked rights — закрыть/запретить соединение и остановить отсылку приватных событий.
- Команда отправки сообщений — **только** `POST /v1/conversations/{id}/messages`. Нет независимого Hub `SendMessage`.
- Server event envelope: `{eventId: uuid, eventVersion: 1, occurredAt: ISO8601, conversationId?: uuid, payload: object}`. SignalR client handler получает envelope одним аргументом (JSON camelCase). Для data events отправка после DB commit через Outbox, возможны повторы/неупорядоченность; дедуп по `eventId` + entity id.

## Hub: методы client → server (Этап 1)

| Метод SignalR `InvokeAsync` / JS `invoke` | Аргументы | Действие / серверная проверка |
|---|---|---|
| `JoinConversation` | `conversationId: string` | AuthZ + присоединить connection к группе |
| `LeaveConversation` | `conversationId: string` | Удалить из группы |
| `SetTyping` | `conversationId: string, isTyping: boolean` | ACL + rate limit + эфемерное уведомление, без хранения |

Прочитанное состояние: **REST** `POST /v1/conversations/{id}/read`, затем серверный event `ConversationReadUpdated`.

## Server → client events (Этап 1)

| Event name (`On`/`on`) | `envelope.payload` | Реакция клиента |
|---|---|---|
| `MessageCreated` | `{message}` | Upsert/dedup message; optimistic reconciliation по `clientMessageId` |
| `ConversationUpdated` | `{conversationId,lastMessagePreview,updatedAt}` | Перечитать summary при необходимости |
| `ConversationReadUpdated` | `{conversationId,readerSide:"CLIENT"|"BUSINESS",lastMessageId}` | Обновить прочитанное |
| `TypingChanged` | `{conversationId,side,isTyping}` | Эфемерный статус, таймаут в UI |
| `BusinessAccessRevoked` | `{businessId,reasonCode}` | Сбросить staff state, очистить приватный cache, обновить permissions |

## WebRTC signaling (Этап 2)

| Направление | Метод/событие SignalR | Данные |
|---|---|---|
| client → server | `AcceptCall` | `callId` |
| client → server | `RejectCall` | `callId, reason?` |
| client → server | `SendOffer` | `callId, sdp` |
| client → server | `SendAnswer` | `callId, sdp` |
| client → server | `SendIceCandidate` | `callId, candidate` |
| client → server | `HangupCall` | `callId` |
| server → client | `CallIncoming` | `{callId,conversationId,kind,expiresAt}` |
| server → client | `CallStateChanged` | `{callId,state:"RINGING"|"CONNECTING"|"ACTIVE"|"ENDED"|"MISSED"|"REJECTED",endedReason?}` |
| server → client | `CallOffer` | `{callId,sdp}` |
| server → client | `CallAnswer` | `{callId,sdp}` |
| server → client | `CallIceCandidate` | `{callId,candidate}` |

All **server→client** events (в том числе вызовы) приходят в описанном envelope; Hub method invocations используют **позиционные аргументы**, согласно списку. Каждое действие требует диалог `CLIENT↔BUSINESS`, соответствующего активного caller/оператора, разрешённого состояния звонка. Только один оператор принимает вызов. SDP/ICE не пишутся в application logs и не сохраняются без отдельной нужды. Сторона WebRTC медиа живёт вне ASP.NET Core Hub; TURN только relay.

## Совместимость

- Эта версия заменяет ранее подготовленный Socket.IO baseline v0.1.0 **до реализации**. Потребители не должны использовать старые event names вида `message.created` или `conversation.join`.
- Backend публикует версии Hub contract + проверяет тестами .NET и JS clients. При будущих breaking changes — новая версия с migration window; любые изменения копируются в три репозитория и согласуются PR.
