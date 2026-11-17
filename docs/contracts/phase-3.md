# Phase 3 — B07 contract 0.7.0

**Статус: утверждённый baseline 0.7.0 для F16/M10, 17 ноября 2026.** Зафиксирован по запросу пользователя на утверждение контрактов перед клиентской интеграцией. Источник REST schema — `artifacts/openapi.json`, события — `events-v1.md`. Результат сверки и границы утверждения: [phase-3-approval.md](phase-3-approval.md). Клиентские репозитории этим утверждением не изменяются.

## Роли

| Действие | OWNER | ADMIN | AGENT | CLIENT |
| --- | --- | --- | --- | --- |
| Читать команду, приглашения и аналитику | Да | Да | Нет | Нет |
| Приглашать/отзывать AGENT | Да | Да | Нет | Нет |
| Приглашать OWNER/ADMIN, менять их роли | Да | Нет | Нет | Нет |
| Менять роль/активность AGENT | Да | Только AGENT→AGENT | Нет | Нет |
| Назначать/снимать ответственного | Да | Да | Нет | Нет |
| Читать назначение своего бизнеса | Да | Да | Да | Нет |
| Читать/отвечать в диалоге компании | Да | Да | Да | Только свой диалог |

Все права требуют активного пользователя, актуальной сессии, active membership и VERIFIED+ACTIVE бизнеса. PLATFORM_ADMIN не становится сотрудником автоматически. Последний активный OWNER не может быть отключён или понижен, включая параллельные запросы. Администратор не может повысить себя. Назначение организационное: не ограничивает чат/звонок единственным оператором и не переносит историю пользователю.

## REST

Все маршруты с префиксом `/v1`. `businessId?:UUID` — query selector для team/my-business endpoints. При одном доступном бизнесе опускается; при неоднозначности 400, при отсутствии подходящих прав 404. `{id}` в members — userId, не отдельный membershipId.

| Метод и путь | Запрос | Ответ |
| --- | --- | --- |
| GET /team | businessId? | TeamMemberView[] |
| GET /my-business/members | businessId?; alias исходного API документа | TeamMemberView[] |
| PATCH /my-business/members/{id} | {role:OWNER/ADMIN/AGENT,isActive:boolean} | TeamMemberView |
| GET /my-business/invitations | businessId? | последние 100 InvitationView, без token/hash |
| POST /my-business/invitations | {email,role} | {invitation:InvitationView,token}; no-store |
| DELETE /my-business/invitations/{id} | businessId? | 204; принятые 409 |
| POST /invitations/accept | {token,password?,displayName?} | TeamMemberView; no-store |
| GET /conversations/{id}/assignment | — | AssignmentView |
| PATCH /conversations/{id}/assignment | {userId:UUID/null,expectedRevision:integer≥0} | AssignmentView; stale revision 409 |
| GET /my-business/analytics | businessId?,from?,to? | BusinessAnalytics |

TeamMemberView = `{userId,displayName,role,isActive}`. InvitationView = `{id,businessId,email,role,expiresAt,acceptedAt,revokedAt}`. AssignmentView = `{conversationId,userId:null/UUID,revision,updatedAt:null/timestamp}`. Отсутствующее назначение возвращается с revision 0, null userId/updatedAt. Снятие назначения повышает revision; `expectedRevision` обязателен по смыслу CAS, стартовое значение 0. Повтор PATCH со старой revision даёт 409; после потери ответа сначала GET. Чужой assignee/диалог — 404.

Приглашение: случайный 256-bit token, в БД только SHA256, срок 48 часов, одно принятие. Создатель передаёт token адресату защищённым каналом вне API; автоматическая рассылка приглашений не заявлена. Не помещать token в логи, analytics или query URL. Лимит 100 приглашений/час/бизнес плюс существующий HTTP rate limit. Отзыв или понижение создателя лишает его ранее выданные privileged invitations права принятия.

Для существующего BUSINESS_MEMBER принятие требует его JWT и совпадения email. CLIENT/PLATFORM_ADMIN не конвертируются в сотрудников. Существующее членство не перезаписывается, включая отключённое: 409, восстановление через PATCH. Для нового email anonymous accept требует password (12–128) и displayName (1–100); создаётся BUSINESS_MEMBER. При RequireConfirmedEmail=true отправляется стандартная проверка email, членство не обходит проверку входа. Недоступная mail queue даёт 503 и откатывает принятие. При false письмо не требуется. Token не означает подтверждение email и не выдаёт auth tokens: затем обычный login. Истёкшее/отозванное/повторно принятое приглашение — 409; неизвестное — 404. При неверной identity — 403.

OpenAPI описывает accept как anonymous-capable endpoint. Для существующего аккаунта клиент всё равно обязан явно передать `Authorization: Bearer ...`; отсутствие обязательного security requirement в schema не отменяет эту ветку проверки. Для нового email запрос выполняется без JWT другого аккаунта. Не делать автоматический повтор создания invitation после потери ответа: token возвращается только один раз, перечитать список и при необходимости отозвать прежнее приглашение.

## Аналитика

BusinessAnalytics = `{from,to,conversations,clientMessages,businessMessages,respondedConversations,unansweredConversations,averageFirstResponseMilliseconds}`. UTC, полуоткрытый интервал `[from,to)`, по умолчанию последние 30 дней, максимум 90. Счётчики messages включают сообщения этого бизнеса внутри интервала независимо от даты создания диалога. Cohort conversations — диалоги, созданные внутри интервала. Для каждого берётся первый CLIENT message до to и первый BUSINESS ответ не ранее него и строго до to. Responded — имеющие такой ответ; unanswered — есть клиентское сообщение, но нет ответа. Пустой диалог не считается unanswered. Среднее только по ответившим; при отсутствии ответов null. Данные на момент to, без прогноза; автоматические ответы от сотрудника считаются BUSINESS сообщениями. Персональная эффективность, online time, SLA и вымышленные метрики не добавлены.

## SignalR и восстановление

Добавлено `ConversationAssignmentChanged` с payload AssignmentView в прежнем envelope eventVersion 1. Только актуальные BUSINESS_MEMBER соединения, подписанные JoinConversation, получают событие; CLIENT не получает ID сотрудника. Outbox записывается атомарно вместе с назначением, возможны повторы/переупорядочивание. Клиент применяет только большую revision, после reconnect повторяет JoinConversation и GET assignment. Для inbox без подписки перечитывает REST. Изменение состава команды обновляется через REST, нового Hub method нет.

## Интеграции и совместимость

Никаких исходящих URL, произвольных webhooks и billing. Имеющихся REST/SignalR достаточно для F16/M10. Новые endpoints/event не меняют прежние чатовые DTO, роли OWNER/ADMIN/AGENT и phase-2 signaling. F16 следует сверить с оригинальным MD в своём репозитории до реализации; M10 — с его исходным заданием. Устройства, iOS и межклиентский E2E не объявляются проверенными backend tests.
