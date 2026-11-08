# B01–B05 — матрица готовности к выпуску

## Решение

- **GO для controlled integration** на временных данных, с одним экземпляром API, установленными миграциями и проверяемым TLS. Доступ ограничен участниками интеграции.
- **NO-GO для public production.** TLS-01 и OPS-01 остаются открытыми P1 на целевой инфраструктуре. Проверка образа Trivy также осталась открытой из-за недоступной базы. Допуск требует выполнения эксплуатационных проверок ниже.

## Исходные замечания аудита

| ID | Приоритет | Замечание и доказательство | Статус | Блокирует публичный запуск |
| --- | --- | --- | --- | --- |
| TLS-01 | P1 | HTTP отклоняется. Проверены известный прокси, HTTPS и WSS в локальном стенде. Сертификаты, forwarded headers и недоступность Kestrel извне в целевой инфраструктуре требуют проверки. [Transport](../../src/chat.Api/DependencyInjection/ProductionTransport.cs), [тесты](../../tests/chat.IntegrationTests/ProductionTransportTests.cs), [JS harness](../../tools/consumer-smoke/signalr-smoke.mjs) | OPEN | Да |
| FILE-01 | P1 | Документы fail closed. Только READY выдаётся и позволяет одобрение. Отрицательный verdict и отказ покрыты тестами. [Сканер](../../src/chat.Api/Businesses/VerificationDocumentScanner.cs), [тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs), [локальный B05.3](B05.3-results.md#проверки) | FIXED | Условие среды ClamAV остаётся обязательным |
| OPS-01 | P1 | Локальный runtime и CI-образ проверяются отдельно от NuGet. Патч реально запущенного целевого процесса и сканирование целевого digest требуют подтверждения. [Образ](../../deploy/Dockerfile), [CI](../../.github/workflows/backend.yml) | OPEN | Да |
| DB-01 | P2 | Readiness отклоняет пустую БД, неполную миграцию и отсутствующую конфигурацию. [Проверка](../../tests/chat.IntegrationTests/PostgresTests.cs) | FIXED | Применить миграции перед допуском |
| API-01 | P2 | Экспорт runtime, схемы ошибок и форматы загрузки проверены. OpenAPI 0.5.0 сравнивается с 0.4.0 и базой PR. [Артефакт](../../artifacts/openapi.json), [тест](../../tests/chat.ContractTests/OpenApiTests.cs) | FIXED | Требуется синхронизация потребителей |
| BIZ-01 | P2 | Непроверенная заявка не резервирует реквизиты. Веб-восстановление OWNER сохраняет историю. Одновременные одобрения, чужой reapply и нейтральный публичный ответ проверены. [Реализация](../../src/chat.Api/Businesses/BusinessService.cs), [тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs) | FIXED | Назначить ответственного за проверку полномочий и споры |
| AUTH-01 | P2 | Лимиты аккаунта и доверенного IP. Пароль проверяется вне блокировки строки. Локальные счётчики связаны с single replica. [Тесты](../../tests/chat.IntegrationTests/AuthHardeningTests.cs) | FIXED | При изменении топологии нужен общий лимитер |
| AUTH-02 | P2 | Replay отзывает семейство, включая победителя конкурентного refresh. Потерянный ответ требует reauth. [Тесты](../../tests/chat.IntegrationTests/AuthHardeningTests.cs), [контракт](../contracts/api-v1.md#авторизация-и-аккаунты--этап-1) | ACCEPTED-LIMITATION | Требуется single-flight в обоих клиентах |
| SYNC-01 | P2 | Состояние прочтения обеих сторон восстанавливается через REST. [Тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs) | FIXED | Нет после consumer sync |
| RT-01 | P2 | Независимая проверка прав закрывает устаревшие соединения. Suspension не отправляет ID или причину. [Тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs) | FIXED | Подтвердить WSS через целевой edge |
| RT-02 | P2 | Одна Production API реплика на PostgreSQL, lock при старте, остановка после его потери. REST catch-up для offline. [Тесты](../../tests/chat.IntegrationTests/WorkerReliabilityTests.cs) | ACCEPTED-LIMITATION | Да для multi-replica |
| MEDIA-01 | P2 | ACL при выдаче и завершении, но выданный GET/PUT остаётся действительным до TTL. [Тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs), [решение](B05.3-results.md#объекты-сканирование-и-отзыв) | ACCEPTED-LIMITATION | Нет при принятии ограниченного остаточного окна |
| MEDIA-02 | P2 | Отказ сканера сохраняет PENDING. Очистка учитывает ссылки и восстанавливается после отката БД. [Тесты](../../tests/chat.IntegrationTests/BusinessMessagingTests.cs) | FIXED | Подтвердить целевые S3/ClamAV и политику хранения |
| OPS-02 | P2 | Короткие аренды, внешние вызовы после commit, повторы и безопасный fencing. Expo может доставить дубль. [Тесты](../../tests/chat.IntegrationTests/WorkerReliabilityTests.cs) | FIXED | Фактический Expo smoke обязателен |
| OPS-03 | P2 | SMTP-намерения шифруются и переживают сбой и перезапуск. Реальная локальная доставка через Mailpit. [Тесты](../../tests/chat.IntegrationTests/WorkerReliabilityTests.cs), [локальный прогон](B05.3-results.md#проверки) | FIXED | Нужен target SMTP smoke |
| P3-DB | P3 | B2C и совпадение диалога enforced составными FK. Предварительный анализ останавливает ошибочный upgrade. [SQL и EF](../../tests/chat.IntegrationTests/IntegrityTests.cs), [миграционный тест](../../tests/chat.IntegrationTests/PostgresTests.cs) | FIXED | Прогнать preflight на восстановленной копии |
| P3-INBOX | P3 | v2 стабилен при новых сообщениях. Старый формат поддерживается. ACL применяется на каждой странице. [Тест](../../tests/chat.IntegrationTests/IntegrityTests.cs) | FIXED | Нет |
| P3-RETENTION | P3 | Технические сроки и bounded cleanup реализованы, Production требует approval. Канонические сообщения и доказательства сохраняются. [Модель](../data-model.md#retention-b054), [тест](../../tests/chat.IntegrationTests/IntegrityTests.cs) | FIXED | Не включать деструктивную политику до согласования |
| P3-DEVICE | P3 | Чужой Expo-токен не перепривязывается без release. Старая доставка подавляется. [Тест](../../tests/chat.IntegrationTests/IntegrityTests.cs) | FIXED | Нужно обновить клиентское переключение |
| P3-CI | P3 | NuGet, snapshot, OpenAPI, Gitleaks, Semgrep и Trivy включены. Локальный Trivy остановился на загрузке базы. Требуются успешный workflow на GitHub и обязательные статусы в защите ветки. [Workflow](../../.github/workflows/backend.yml), [результаты](B05.4-results.md#проверки) | OPEN | Да до scan образа и подтверждения зелёного pipeline |
| P3-LAYERS | P3 | Повторное построение DTO вынесено в Application. Оркестрация транзакций остаётся в Api, где уже расположены EF access checks. [Общий маппинг](../../src/chat.Application/Messaging/MessagePresentation.cs) | ACCEPTED-LIMITATION | Нет |
| P3-NEGATIVE | P3 | Проверены issuer/audience/expiry, конкуренция refresh, Join/Leave/Typing, read receipt и suppression отзыва. JS Node harness проверяет transport. Browser, React Native и синхронизация в отдельных репозиториях остаются открытыми. [Тесты](../../tests/chat.IntegrationTests), [harness](../../tools/consumer-smoke/signalr-smoke.mjs) | OPEN | Да для заявленной поддержки клиентов |
| SEC-REPO | P3 | Сканируется текущий срез и достижимая история с маскированием. Данные в БД, внешних логах и удалённых refs требуют отдельных проверок. [Проверки](B05.4-results.md#проверки) | FIXED | Отдельно подтвердить хранилища секретов |

## Принятые P2 ограничения

| ID | Роль владельца | Мера и повод пересмотра |
| --- | --- | --- |
| AUTH-02 | Владелец клиентской авторизации | Single-flight, атомарное сохранение пары, reauth после неизвестного исхода. Пересмотр при росте повторных входов после refresh либо изменении threat model. |
| RT-02 | Владелец эксплуатации | Stop-first, один writable primary, алерты lag, REST resync. Пересмотр при необходимости второй реплики или устойчивом отставании очереди. |
| MEDIA-01 | Владелец безопасности | Короткие TTL, ACL перед выдачей и запрет завершения из отозванного семейства. Пересмотр при требовании немедленного отзыва или инциденте с остаточным доступом. |

## Production dependencies

| Зависимость | Локальные доказательства | Условие public GO |
| --- | --- | --- |
| Реплики | Реальный PostgreSQL проверил конфликт владельцев startup lock и остановку при его потере | Один API и stop-first. Не включать несколько реплик |
| HTTPS/reverse proxy | Production TestServer и официальный JS-клиент через изолированный Nginx/Kestrel с WSS и проверкой маскирования логов | Провести HTTP-deny, HTTPS login, WSS, peer allowlist, network isolation, обе цепочки доверия и redaction на цели |
| .NET runtime/container | SDK 10.0.403 и образ на ASP.NET Core 10.0.14 | Проверить актуальный security patch и Trivy целевого digest. Учитывать отдельно runtime хоста при запуске вне контейнера |
| PostgreSQL restore | Fresh upgrade, upgrade с данными, ошибка preflight на искусственной порче и локальный `pg_dump`/`pg_restore` с синтетическими диалогами | Восстановить репрезентативный backup с S3, key material и согласованными RPO/RTO, повторить миграцию |
| Expo | Контролируемый шлюз, lease и подавление старого владельца | Реальные Expo tickets/receipts и получение на Android/iOS без утечки приватного содержимого |
| MinIO/S3 и ClamAV | B05.3 реальный локальный stack, fail-closed и уборка, ClamAV FOUND | Проверить подписи, storage IAM, GET/PUT, READY/quarantine и удаление в целевой среде |
| SMTP | Durable intent, отказ/повтор/перезапуск, Mailpit | Проверить STARTTLS, отказ и восстановление, стабильный encryption key и мониторинг dead letters |
| Контракты | OpenAPI и тесты backend, JS harness | Два отдельных consumer PR, smoke реальных клиентов и сверка с опубликованными артефактами |
| CI и retention | Локальные команды и workflow | Обязательные успешные статусы в защите ветки, утверждённая политика и действующий hold для БД/S3/backup |

## Передача в потребители

1. **chat-frontend:** обновить генерацию по `artifacts/openapi.json` версии 0.5.0 и скопировать оба контракта. Добавить веб-восстановление OWNER с новым черновиком, нейтральный экран подтверждения публичной подачи и обработку спора администратором. Сохранить отказ для чужих, pending и suspended состояний.
2. **chat-mobile:** обновить типы по тому же артефакту. Бизнес-регистрация и reapply остаются в вебе. Перед сменой аккаунта выполнить release устройства до logout, обработать `409` и зарегистрировать токен текущей сессии. Проверить Expo на двух физических устройствах.
3. **Оба:** держать курсор непрозрачным, сбрасывать его при reconnect, выполнить single-flight refresh и REST reconcile с `readState`. Отрабатывать disconnect и запрещённые Hub methods даже без `BusinessAccessRevoked`. Проверить bearer transport, событие `ConversationReadUpdated` и очистку приватного cache.
4. **Оба:** выполнить каждый PR в соответствующем репозитории, сохранить вывод локального [harness](../../tools/consumer-smoke/signalr-smoke.mjs) как вспомогательный результат, затем проверить native/browser сборку, WSS и logout на staging. Фактические потребители в рабочей копии отсутствуют.

