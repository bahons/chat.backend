# B07 — Backend, этап 3

Дата: 17 ноября 2026. Выполнен исходный `prompts/07-business-operations.md` в chat-backend. Изменения локальные, без production deploy B07. F16, M10, B08 и межклиентский E2E не начаты.

## Реализовано

- Управление командой: GET team/members, приглашение/список/принятие/отзыв, изменение роли и активности. Существующие OWNER/ADMIN/AGENT сохранены. ADMIN управляет только AGENT, OWNER — всеми ролями. Последний активный OWNER защищён, включая гонки.
- Одноразовые invitations на 48 часов, 256-bit secret и SHA256 at rest, без токенов в списках/аудите. Существующий BUSINESS_MEMBER принимает только под собственным JWT. Новый сотрудник создаётся по приглашению; стандартная email verification не обходится. Исходящий webhook/mail invitation transport не добавлен: безопасная передача token остаётся у создателя.
- Assignment существующего B2C-диалога: GET и PATCH с обязательной expectedRevision, один победитель при гонке. Назначать можно активного сотрудника этого же бизнеса; отключение сотрудника атомарно снимает назначения. История и прежние chat/call ACL принадлежат компании, назначение не создаёт частную переписку оператора.
- `ConversationAssignmentChanged` в transactional Outbox и прежнем SignalR envelope, только текущим сотрудникам, подписанным на диалог. CLIENT исключён; reconnect восстанавливает состояние через GET и revision.
- PostgreSQL аналитика по реальным timestamps канонических Messages: обращения, клиентские/бизнес-сообщения, ответившие/неотвеченные обращения и среднее время первого ответа. UTC `[from,to)`, максимум 90 дней; без фиктивных SLA/online/персональных показателей и дублирования истории.
- AuditLogs изменений команды/назначений. Произвольных webhooks, внешних URL, billing и тарифных ACL нет: клиентам достаточно REST/SignalR.

## Контракты до клиентской интеграции

Backend OpenAPI **0.7.0** экспортирован в `artifacts/openapi.json`. Полные DTO, маршруты, таблица прав, семантика аналитики, приглашений и reconnect: [phase-3](docs/contracts/phase-3.md). Описание REST/events обновлено. Исходное расхождение B07 `GET /v1/team` и API-документа `/my-business/members` разрешено alias, без удаления маршрутов.

Новые endpoints: GET `/v1/team`, GET/PATCH `/v1/my-business/members[/{id}]`, GET/POST/DELETE `/v1/my-business/invitations[/{id}]`, POST `/v1/invitations/accept`, GET/PATCH `/v1/conversations/{id}/assignment`, GET `/v1/my-business/analytics`. Новых Hub methods нет. Единственное новое событие: `ConversationAssignmentChanged`.

Контракт утверждён как baseline для F16/M10 17 ноября 2026 по запросу пользователя; [результат сверки](docs/contracts/phase-3-approval.md). Синхронизация потребителей ещё не выполнена. До F16 сверить его оригинальный MD в frontend; M10 также сверить с исходным заданием mobile. Для интеграции использовать настоящий backend с миграцией B07, не mock adapters.

## EF Core

Миграция `20261117155040_TeamAssignments` добавляет TeamInvitations и ConversationAssignments, FK/CHECK/индексы, уникальный `(Conversation.Id,BusinessId)` и индекс `Messages(ConversationId,AuthorSide,CreatedAt)`. Составные FK не допускают cross-business assignment. Snapshot синхронизирован, idempotent SQL создан в игнорируемом `TestResults/b07-migrations.sql`.

Миграции проверены на настоящем временном PostgreSQL: отсутствие pending model changes, upgrade с сохранением существующего сообщения/read receipt, downgrade к baseline и повторный upgrade. Production schema B07 не менялась. Down удаляет invitations/assignments и требует остановки трафика/backup перед реальным использованием.

## Фактические проверки

`dotnet restore`, `dotnet build --no-restore -warnaserror`, `dotnet test --no-build`, `dotnet format chat.sln --no-restore --verify-no-changes` и EF pending-model/idempotent SQL checks прошли. Полный прогон: **64 теста**, 12 unit + 1 contract + 51 integration, без пропусков. Логи `TestResults/b07-*.log`, TRX в `tests/*/TestResults/b07-final_*.trx` (игнорируются Git).

B07 integration test с настоящим PostgreSQL и совместимым .NET SignalR client проверяет роли, cross-business access/assignee, replay/concurrent accept, expired/revoked invitations, отключённого создателя, создание нового неподтверждённого сотрудника с verification mail intent, обязательную revision, concurrent assignment, конкурентное понижение двух OWNER, доступ после отзыва и закрытие Hub, снятие назначения, сохранность истории компании, точное время ответа 60000 ms, запрет аналитики агенту и отсутствие assignment event у CLIENT. Предыдущие B01–B06 integration tests, включая WebRTC signaling .NET/JS, также прошли.

OpenAPI schema lint Redocly 2.76.0: PASS. Oasdiff 1.34.1 относительно HEAD 0.6.0: `[]`, новых breaking changes нет. Относительно установленного baseline 0.4.0 проходят только прежние 16 разрешённых B06 enum extensions; новые исключения не добавлялись.

Физические WebRTC звонки, клиенты F16/M10, Android/iOS E2E и production B07 не тестировались и не имеют статуса PASS.

## Изменённые файлы

- `src/chat.Domain/Businesses/TeamModels.cs`, `src/chat.Contracts/Businesses/TeamContracts.cs`.
- `src/chat.Api/Businesses/TeamService.cs`, `Endpoints/TeamEndpoints.cs`, `Program.cs`, `Endpoints/ApiDocumentation.cs`, `Messaging/OutboxWorker.cs`.
- `src/chat.Infrastructure/Persistence/{ChatDbContext,MessagingConfiguration,TeamConfiguration}.cs`, миграция TeamAssignments + Designer + snapshot.
- `tests/chat.IntegrationTests/TeamTests.cs`, `tests/chat.ContractTests/OpenApiTests.cs`.
- `Directory.Build.props`, `artifacts/openapi.json`, `README.md`, `docs/{architecture,data-model,security,implementation-status}.md`, `docs/contracts/{api-v1,events-v1,phase-3}.md`, этот отчёт.

## Открытые риски и следующие проверки

1. Утверждение baseline закрыто 17 ноября: ADMIN управляет только AGENT, назначение организационное, invitations передаются вручную, CLIENT account не конвертируется. Следующий этап — F16/M10 по их исходным MD с этим baseline.
2. B07 пока не развёрнут. Существующий production остаётся на разрешённом B06; backup/restore, migration dry-run и отдельный согласованный выпуск нужны до production клиентской интеграции.
3. Сериализация team mutations по бизнесу обеспечивает корректность, но ограничивает пропускную способность горячего бизнеса. Максимальный размер команды, fanout при массовом отзыве и планы SQL на реальном объёме требуют B08 нагрузок. Проверка тестовой аналитики не заменяет benchmark.
4. Сроки хранения invitations/assignment/audit, off-host backup и disaster recovery остаются эксплуатационными задачами. Автоматическая очистка новой истории не добавлена.
5. Сохраняются single-replica SignalR и короткое окно между ACL проверкой и сетевой доставкой, eventual sweep и ограничения отзыва TURN/P2P B06.
6. F16/M10: проверить выбор бизнеса, роли, invitation accept/login/email verification, повтор после потери ответа, revision reconciliation, смену ответственного двумя операторами, отзыв прав, точную трактовку диапазона аналитики. Затем Web/Android/iOS E2E и регрессия сообщений/WebRTC.
