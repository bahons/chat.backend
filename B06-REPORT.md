# B06 — AUDIO/VIDEO и WebRTC signaling via SignalR

Дата: 16 ноября 2026. Источник задания: `prompts/06-webrtc.md`. Статус: backend реализован и локально проверен; физические WebRTC-звонки и готовность целевой инфраструктуры **не подтверждены**.

## Исходная готовность и архитектура

Прочитаны AGENTS.md, README, implementation-status, architecture, security, data-model, REST/events contracts и реализация B05. До изменений phase-2 таблицы описывали требуемые имена, но runtime Call endpoints, state machine и signaling не были реализованы. Имена из оригинального events-v1 сохранены. Использован существующий монолит Domain/Application/Infrastructure/Contracts/Api, PostgreSQL, MessagingHub, реестр подключений и Outbox. Альтернативных CALL-01/CALL-02 нет.

## Реализованные требования

| Требование | Реализация и границы |
| --- | --- |
| AUDIO/VIDEO | MIME, размер до 50 МиБ, контейнер/codecs/stream metadata через ffprobe; audio до 300 с, video до 120 с и 1920×1080. Duration/width/height/codecs включены в сообщение. После probe обязателен ClamAV, выдача только READY с текущим ACL. PDF/JPEG/PNG сохраняют лимит 10 МиБ. |
| State machine | RINGING → CONNECTING → ACTIVE; REJECTED/MISSED/ENDED terminal. RINGING 45 с, CONNECTING 60 с, hard cap два часа от создания. Повторный offer поддерживает renegotiation/reconnect; revision растёт. ACTIVE означает полученный answer, не подтверждение RTP. |
| Конкурентность | Транзакции и одинаковый порядок блокировок conversation → call; уникальный partial index одного live Call; один выбранный сотрудник и семейство. clientCallId обеспечивает повтор создания без дубля. |
| ACL | Только CLIENT↔BUSINESS в существующем разрешённом диалоге active VERIFIED компании, актуальные account/session/membership. PLATFORM_ADMIN и чужие пользователи не становятся участниками. После выбора остальные сотрудники не получают SDP/ICE/credentials. Отзыв завершает Call, права пересматриваются при действии/доставке и worker sweep. |
| Signaling | Исходные шесть методов и пять событий, прежний envelope/eventVersion 1. Incoming/state — через транзакционный Outbox, SDP/ICE — эфемерно только другой выбранной стороне, без хранения. Нет записи, API media relay или group calls. |
| Ограничения | До пяти созданий в минуту на инициатора; до 60 Hub call commands за 10 с на connection плюс общий HTTP limiter. SDP до 24 КиБ UTF-8, ICE candidate до 2048 символов, Hub message до 32 КиБ. Исключения нейтральные, SDP/ICE/credentials не добавлены в логи. |
| TURN | Coturn REST HMAC-SHA1, отдельный secret, TTL 60–600 с (default 300) и не дольше Call expiry; ICE только выбранным семействам CONNECTING/ACTIVE, no-store. Без config создание/ICE 503; при enabled без config readiness 503. |
| Совместимость | Старые REST/Hub chat operations сохранены, прошёл полный прежний набор тестов. Новые nullable metadata и enum расширения описаны до интеграции. |

ffprobe инспектирует metadata, а не полностью декодирует и не перекодирует файл. Антивирус, resource isolation и актуальные пакеты остаются обязательными. Race между последней ACL-проверкой и сетевой отправкой полностью не устраняется: уже отправленные байты отозвать нельзя.

## EF Core migration

`20261115194749_CallsAndTimedMedia` добавляет Calls, nullable metadata Attachments, composite FK на правильный client/business диалога, unique live index, unique инициатор/clientCallId, индексы TTL и CHECK состояния/типа/выбранного оператора/terminal timestamps. Проверка NULL у бизнес-инициатора отдельным тестом возвращает PostgreSQL check violation.

`dotnet ef migrations has-pending-model-changes` подтвердил согласованность snapshot. Сформирован idempotent SQL `TestResults/b06-migrations.sql`. PostgreSQL integration test применяет миграции на пустой БД, обновляет legacy schema с данными, проверяет pending readiness, откатывает до первой миграции и применяет заново. Применения в production не было. Перед production нужен backup и проверка на копии данных; Down удаляет Calls и новые metadata.

## REST endpoints

| Метод | Endpoint | Назначение |
| --- | --- | --- |
| POST | `/v1/calls` | conversationId, AUDIO/VIDEO kind, optional clientCallId; CallView |
| GET | `/v1/calls/{id}` | актуальное состояние, сроки, revision, canAccept/canSignal |
| POST | `/v1/calls/{id}/end` | идемпотентное завершение |
| POST | `/v1/calls/{id}/ice-config` | краткоживущая конфигурация ICE |

CallView не публикует ID оператора/семейств. ErrorEnvelope прежний; чужой ресурс 404, конфликт 409, лимит 429, отсутствующий TURN/неготовая зависимость 503. Авторизация обязательна.

## SignalR methods/events

Hub: `/hubs/messaging`. Методы: `AcceptCall(callId)`, `RejectCall(callId, reasonOrNull)`, `SendOffer(callId, sdp)`, `SendAnswer(callId, sdp)`, `SendIceCandidate(callId, candidate)`, `HangupCall(callId)`. Аргументы позиционные; для RejectCall явно передать второй nullable аргумент. Текст reason не сохраняется.

События: `CallIncoming`, `CallStateChanged`, `CallOffer`, `CallAnswer`, `CallIceCandidate`. Исходный envelope сохранён; state/SDP/ICE получили additive revision, state — expiresAt. ICE candidate — объект candidate/sdpMid/sdpMLineIndex/usernameFragment. Точные формы: `docs/contracts/events-v1.md`.

Только инициатор посылает offer, выбранный собеседник отвечает после offer; ICE — выбранные стороны CONNECTING/ACTIVE. При reconnect в том же refresh family прочитать REST, инициатор повторяет offer, peer — answer и новые ICE. SDP не восстанавливается из БД. События состояния могут повторяться/запаздывать: dedup eventId и сравнение revision.

## STUN/TURN и инфраструктура

Настройки `Calls:Enabled`, `TurnSecret`, `TurnUrls`, `StunUrls`, `CredentialSeconds`; `Media:ProbePath`. Calls по умолчанию выключены. TURN нужен отдельно с публичным адресом, listener UDP/TCP, TLS certificate, relay port range/firewall, quotas, синхронизацией времени и ограничением peer destinations. API не заменяет coturn.

Полный runbook: `docs/webrtc-operations.md`. Production compose сохраняет HTTPS до Kestrel, доверенный proxy и изоляцию прямого доступа; добавлены opt-in env и tmpfs 192 МиБ. В Dockerfiles установлен ffmpeg. Это подготовка конфигурации, а не выполненный деплой.

## Фактически выполненные проверки

SDK 10.0.403, Windows runtime 10.0.14; реальный PostgreSQL 17 в Docker. Команды выполнялись из backend с изолированным SDK и `CHAT_TEST_FFPROBE`, указывающим на установленный ffprobe.

| Команда/проверка | Результат |
| --- | --- |
| `dotnet restore chat.sln` | PASS |
| `dotnet build chat.sln --no-restore -warnaserror` | PASS, 0 warnings/errors |
| `dotnet test chat.sln --no-build` | PASS: 63, failed 0, skipped 0; 12 unit + 1 contract + 50 integration |
| `dotnet format chat.sln --no-restore --verify-no-changes` | PASS |
| EF tool restore, pending-model, idempotent script | PASS |
| Redocly 2.76.0 OpenAPI lint | PASS |
| Strict oasdiff 1.34.1 против 0.4.0 | 16 ERR: только требуемые response enum расширения; 2 WARN: расширение request regex. Без исключений strict gate не проходит. |
| Точечный B06 contract gate | PASS: допускает только точные 16 findings; synthetic removal негативно отклонён. Исходный baseline не менялся. |
| npm ci / audit, JS syntax | PASS, 0 vulnerabilities |
| Gitleaks 8.30.1 worktree scan / git diff --check | PASS, секреты не найдены в проверенном срезе; история/внешние системы этим прогоном не проверялись |
| Production Dockerfile local build | PASS, image `chat-backend:b06-local`; фактически .NET/AspNetCore 10.0.14, ffprobe 6.1.1-3ubuntu5. Image не развёрнут. |

Новые тесты проверяют state transitions, TTL и TURN HMAC; MIME/metadata negatives; реальную инспекцию ffprobe 9.0.2 с AAC/H.264 и отклонение 301-секундного audio; quarantine/READY, привязку ровно одного media, download ACL и прежний PDF limit. В этом прогоне object storage, ClamAV и push — тестовые адаптеры; он не подтверждает доступность production S3/clamd/Expo.

Совместимые реальные SignalR clients: .NET SignalR и JS `@microsoft/signalr` 10.0.11. JS поднимает настоящий локальный Kestrel HTTPS/WSS, доверяет только сгенерированному test certificate и использует отдельную PostgreSQL БД/один сервер. Проверены incoming, concurrent accept ровно одного сотрудника, чужие вызовы/проигравший сотрудник, offer/answer/ICE, TURN credential retrieval, reconnect/renegotiation и hangup. Дополнительно проверены бизнес-инициатор, payload/rate limit, rejection, ringing/connecting expiry, suspension, logout и повтор end/create.

Первоначальные диагностические прогоны падали из-за тестового Windows TLS key, двух серверов с общей Outbox и fixture login rate limit. Исправлено тестовое окружение без ослабления production TLS/ACL. Финальный полный прогон зелёный. Логи команд — игнорируемые `TestResults/b06-*.log`, TRX — `tests/*/TestResults/b06-final_*.trx`.

## Изменённые файлы B06

- Новые `src/chat.Domain/Calls/Call.cs`, `src/chat.Contracts/Calls/CallContracts.cs`, `src/chat.Api/Calls/{CallOptions,CallService,CallDelivery,CallWorker}.cs`, `src/chat.Api/Endpoints/CallEndpoints.cs`.
- Новые `src/chat.Application/Media/IMediaProbe.cs`, `src/chat.Infrastructure/Media/FfprobeMediaProbe.cs`, `src/chat.Infrastructure/Persistence/CallConfiguration.cs`.
- `src/chat.Infrastructure/Persistence/Migrations/20261115194749_CallsAndTimedMedia{,.Designer}.cs`, `ChatDbContextModelSnapshot.cs`, `ChatDbContext.cs`, `DeliveryConfiguration.cs`.
- `src/chat.Domain/Messaging/{MessagingModels,DeliveryModels}.cs`, `src/chat.Contracts/Messaging/{MessageContracts,DeliveryContracts}.cs`, `src/chat.Application/Messaging/MessagePresentation.cs`.
- `src/chat.Api/Messaging/{MediaService,MediaServices,ConversationService,OutboxWorker,RealtimeConnections}.cs`, `Hubs/MessagingHub.cs`, `DependencyInjection/BootstrapServices.cs`, `Endpoints/{ApiDocumentation,ReadinessConfigurationCheck}.cs`, `Program.cs`.
- `tests/chat.UnitTests/CallTests.cs`, `tests/chat.IntegrationTests/{CallTests,BackendFixture,PostgresTests}.cs`, `tests/chat.ContractTests/OpenApiTests.cs`, `tools/consumer-smoke/b06-smoke.mjs`.
- `tools/contracts/{verify-breaking.mjs,b06-enum-extensions.json}`, `.github/workflows/backend.yml`, `Directory.Build.props`, `artifacts/openapi.json`.
- `deploy/{Dockerfile,Dockerfile.production,docker-compose.production.yml}`, `.env.production.example`.
- `README.md`, `docs/{architecture,security,data-model,implementation-status,webrtc-operations}.md`, `docs/contracts/{api-v1,events-v1}.md`, `B06-REPORT.md`.

В рабочей копии до B06 уже были изменения auth/business/push/Swagger/production config и тестов. Они сохранены; общий git diff шире B06, не все изменения относительно HEAD относятся к этому заданию. Репозитории chat-frontend/chat-mobile не изменялись.

## Оставшиеся блокеры и риски

1. **Физические browser↔mobile звонки, реальные устройства и TURN relay fallback НЕ ПРОВЕРЕНЫ.** Ни signaling PASS, ни ACTIVE не доказывают звук/изображение. Реальной TURN allocation в этом прогоне нет.
2. Целевой reverse proxy/TLS/WSS, coturn DNS/certificates/firewall, production runtime patch и security scan нового image нужно проверить перед выпуском. Production не обновлялся.
3. Строгие consumer enums требуют согласованного F15/M09 обновления до появления новых media. Документированное исключение CI не делает старые clients совместимыми.
4. Уже выданные TURN credentials годны до expiry, allocations и P2P могут жить дольше при неверной инфраструктуре/клиенте. Клиенты обязаны закрывать tracks; мгновенного сетевого отзыва здесь нет.
5. ffprobe metadata inspection не гарантирует полную декодируемость или отсутствие decoder vulnerabilities. Нужны OS memory/CPU/egress limits, обновления пакетов, нагрузочные проверки и clamd лимиты для 50 МиБ. Linux media flow с runtime image отдельно не прогонялся; image build и executable version проверены.
6. Остаётся single-replica модель B05. Worker batch/DB backlog увеличивает задержку TTL/revocation. Эфемерные SDP/ICE могут потеряться при disconnect; REST reconciliation и renegotiation обязательны. Call retention пока без автоматического удаления, согласовать сроки.

## Проверки существующих F15 и M09

- Сверить OpenAPI 0.6.0, enums AUDIO/VIDEO, metadata и MIME/codecs с фактическими записями браузера/мобильного recorder; несовместимый формат не должен обходить quarantine.
- Сверить исходные method/event names, positional args (особенно RejectCall с null), envelope, revision, expiresAt, canAccept/canSignal; не придумывать альтернативных имён.
- Проверить UI конкурентного принятия: проигравший сотрудник закрывает invitation и не получает media/ICE; чужие Calls и новая login family не перехватывают активный звонок.
- Проверить reconnect через GET Call, повтор offer инициатором, answer и новые ICE, игнорирование старых revision; busy/reject/missed/timeout/hangup/logout/suspension очищают media tracks и UI.
- Проверить microphone/camera permissions, смену camera/audio route, background/foreground и сеть на настоящих устройствах.
- Browser↔mobile AUDIO и VIDEO в обе стороны; принудительный relay-only и подтверждение selected relay candidate pair, UDP/TCP/TLS fallback, обновление credentials и NAT-сценарии.

Изменения клиентов оформляются отдельно. На момент исходного отчёта деплой на chat.dodapp.kz не выполнялся. B07 не начат.

## Дополнение: разрешённый production deploy 16–17 ноября 2026

После отдельного разрешения владельца B06 установлен на целевом сервере. API 0.6.0, миграция, runtime 10.0.15, HTTPS/readiness, авторизованный WSS и реальные внешние TURN allocation/relay UDP/TCP/TLS подтверждены. Сохранены site-specific конфигурация и данные, backup проверен восстановлением и миграцией копии. Исходные пункты выше о непроверенной целевой инфраструктуре заменены этими фактическими результатами; физические audio/video звонки по-прежнему не имеют PASS.

Изменения выпуска: `deploy/Dockerfile.production`, `deploy/docker-compose.webrtc.yml`, `deploy/production/{release.sh,renew-turn-tls.sh,turnserver.conf.template}`, `src/chat.Api/Identity/{IdentityOptions.cs,IdentityServices.cs,AccountMailQueue.cs}`, `src/chat.Api/Endpoints/ReadinessConfigurationCheck.cs`, `tests/chat.IntegrationTests/PostgresTests.cs` и эксплуатационная документация. Новых API и миграций сверх B06 не добавлено. Явный email opt-out сохраняет уже существовавшую конфигурацию сервера; test проверяет readiness и отказ постановки писем в отключённую очередь.

Точные версии, инфраструктура, команды проверки, backup, release incident и оставшиеся ручные сценарии F15/M09: [docs/deployments/B06-2026-11-17.md](docs/deployments/B06-2026-11-17.md).
