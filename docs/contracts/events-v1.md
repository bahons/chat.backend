# Realtime contract — ASP.NET Core SignalR (v0.5.0)

**Статус:** baseline, одинаковый в backend/frontend/mobile. Маршруты и события этапа 1 реализованы в B05. Сигналинг звонков запланирован на B06.

**Технология:** **ASP.NET Core SignalR**, Hub `https://<api-host>/hubs/messaging`, TLS/WSS, SignalR protocol JSON. Browser/server .NET клиент: `Microsoft.AspNetCore.SignalR.Client`; React Native клиент: официальный `@microsoft/signalr` после проверки RN/transport compatibility. Имена Hub methods и клиентских событий ниже чувствительны к контракту и тестируются кросс-клиентски.

## Авторизация, соединения и доставка

- AuthN JWT/cookie according to client. На backend верифицировать user, membership, business verification, revocation. `conversationId` не даёт права доступа.
- Web Blazor Server: SignalR `.NET` client из server-side scoped session к backend, user-scoped токен **не отдавать в browser JS**. Держать ограниченное число connections и освобождать по circuit/session lifecycle.
- React Native: `withUrl(hubUrl, { accessTokenFactory })`; проверить поведение токенов конкретного SignalR transport. Если access token попадает в query string во время WebSocket/SSE handshake, принимать **только на Hub path**, требовать WSS, short-lived token, **не логировать query string**; не вставлять токены вручную в URL.
- После reconnect подпишитесь на события, повторите `JoinConversation` для открытых чатов и перечитайте REST-список, детали и историю открытых диалогов. Сливайте события, принятые во время чтения REST, с полученным снимком.
- `JoinConversation` регистрирует подписку в памяти API после проверки ACL. `LeaveConversation` и `SetTyping` проверяют текущую сессию и ACL. `SetTyping` публикуется только для активной подписки, до одного события в секунду с соединения.
- Допускаются до 256 подключений на API, 4 на аккаунт и 16 подписок на подключение. При отказе освободите старое подключение или подписку перед повтором. Проверка действительности сессии и прав идёт независимо от Outbox каждые две секунды с трёхсекундным бюджетом проверки. При ошибке проверки соединения закрываются. При устойчивом отзыве рабочий ориентир до пяти секунд с учётом бюджетов, плюс время завершения транспорта.
- Команда отправки сообщений — **только** `POST /v1/conversations/{id}/messages`. Нет независимого Hub `SendMessage`.
- Server event envelope: `{eventId: uuid, eventVersion: 1, occurredAt: ISO8601, conversationId?: uuid, payload: object}`. Handler получает envelope одним аргументом. Outbox повторяет отправку в пределах окна, возможны повторы и переупорядочивание. При отсутствии подходящих соединений событие помечается `offline`, восстановление идёт через REST. Успешный вызов SignalR подтверждает передачу транспорту без подтверждения от приложения клиента. Дедуплицируйте по `eventId` и id сущности.
- B05.4 восстанавливает `MessageCreated` и `ConversationUpdated` из сохранённого сообщения. Outbox хранит ссылку, форма событий и `eventVersion: 1` сохранены. При неоднозначной ссылке старого события применяется REST-восстановление.

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
| `ConversationReadUpdated` | `{conversationId,readerSide,lastMessageId,updatedAt}` | Обновить позицию стороны `CLIENT` или `BUSINESS` |
| `TypingChanged` | `{conversationId,side,isTyping}` | Эфемерный статус, таймаут в UI |
| `BusinessAccessRevoked` | `{businessId,reasonCode}` | Историческое событие. B05.3 завершает соединение после утраты ACL без публикации причины или business ID |

При suspension активные клиенты и сотрудники теряют доступ к подписке на соответствующий диалог.
Клиент без такой подписки не получает отдельного события. При потере соединения обновите сессию,
проверьте доступ и очистите закрытые данные по ответам REST. Наличие события отзыва для этого
сценария не гарантируется. Старые записи `BusinessAccessRevoked` в Outbox подавляются.

Для каждого диалога храните две позиции из REST `readState.client` и `readState.business`.
Сравнивайте `updatedAt` только внутри одной стороны диалога. Дедуплицируйте по `eventId` и применяйте
лишь более новую позицию независимо от порядка событий и REST-ответов. `null` из запоздавшего
снимка не должен сбрасывать известную позицию. Сохраняйте точность timestamp при сравнении,
поскольку соседние обновления могут различаться на микросекунду. При событии из старого Outbox
без `updatedAt` перечитайте детали по REST. После `403` или `404` очистите недоступные данные диалога.
В версии 0.3.0 поле `updatedAt` добавлено к существующему событию. Имена, envelope и `eventVersion: 1` сохранены.

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

- Используйте имена методов и событий из таблиц этого контракта. При reconnect выполняйте REST reconciliation и дедупликацию по `eventId`.
- Backend публикует версии Hub contract + проверяет тестами .NET и JS clients. При будущих breaking changes — новая версия с migration window; любые изменения копируются в три репозитория и согласуются PR.
