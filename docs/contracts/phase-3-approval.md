# Утверждение phase-3 контрактов перед F16/M10

Дата: 17 ноября 2026. По запросу пользователя зафиксирован baseline backend **0.7.0** для интеграции Vue Web F16 и Expo Mobile M10. Выполнена сверка `TeamEndpoints`, `TeamContracts`, `TeamService`, Outbox delivery, OpenAPI и документации. Различий, требующих изменения публичного API, не обнаружено. Код API и миграции в ходе утверждения не изменялись.

## Согласованный объём

- REST: 10 операций team/members, invitations, assignment и analytics из [phase-3.md](phase-3.md); GET team и members — aliases. DTO, nullable fields, обязательные isActive/userId/expectedRevision и коды ошибок определяются [OpenAPI](../../artifacts/openapi.json).
- Роли: OWNER управляет всеми ролями; ADMIN — только AGENT; AGENT не управляет командой, назначением и аналитикой. Последний активный OWNER защищён. Все решения требуют текущего бизнес-ACL.
- Назначение обозначает ответственного, история остаётся у компании. Все разрешённые сотрудники сохраняют доступ к чатам и phase-2 звонкам. PATCH использует expectedRevision, конфликт 409, после потери ответа — GET.
- Invitations одноразовые, срок 48 часов, передаются создателем защищённым каналом. Автоматическая рассылка не входит в baseline. Существующий BUSINESS_MEMBER принимает с собственным JWT; CLIENT не конвертируется. Новый сотрудник проходит действующий режим email verification, затем обычный login.
- SignalR: единственное новое событие `ConversationAssignmentChanged`, прежний envelope/eventVersion 1, только BUSINESS_MEMBER с подпиской на диалог и актуальным ACL. CLIENT не получает employee ID. Нет нового Hub method. Дедупликация и revision/REST reconciliation обязательны.
- Analytics: UTC `[from,to)`, 30 дней по умолчанию, до 90 дней; cohort по дате создания диалога, first response по timestamps сообщений, null average без ответов. Счётчики сообщений считаются по интервалу независимо от cohort. Персональных KPI/SLA нет.

## Подтверждение

В B07 фактически прошли 64 теста (12 unit, 1 contract, 51 integration), restore/build/format verify и EF checks, PostgreSQL настоящий. Role revoke, cross-business, concurrent accept/assignment/last-owner, реальный .NET SignalR recipient filtering и метрики проверены. Redocly schema lint прошёл; oasdiff относительно 0.6.0 не обнаружил новых breaking changes. В этом шаге повторный contract snapshot test прошёл (1/1); дополнительно проверены все 10 операций и обязательные поля CAS/isActive в OpenAPI. Лог: игнорируемый `TestResults/phase-3-approval-contract.log`. Полный прогон в этом шаге не повторялся.

Для сопоставления consumer snapshots фиксируется [manifest](phase-3-baseline.json): SHA256 OpenAPI после JSON.parse/JSON.stringify и SHA256 событий после нормализации CRLF→LF. Такой способ не зависит от перевода строк при checkout. Изменение baseline требует обновления версии/документации, проверок и согласованного обновления обоих consumers.

## Перед стартом F16/M10

1. Прочитать исходный F16 MD и M10 MD в соответствующих репозиториях. При расхождении с baseline сначала зафиксировать разницу, не придумывать новые event names/permissions.
2. Скопировать/сгенерировать независимые типы из OpenAPI 0.7.0, синхронизировать events и manifest. Не импортировать исходники другого репозитория.
3. Использовать настоящий экземпляр B07 с миграцией `20261117155040_TeamAssignments`; проверить OpenAPI info.version и readiness. Существующий production пока B06, на нём phase-3 endpoints отсутствуют. Утверждение контракта не является разрешением production deploy B07.
4. Проверить роли, выбор бизнеса, приглашение/принятие, CAS и reconnect, отзыв прав и точную семантику аналитики на API. Затем Web/Android/iOS E2E, регрессию сообщений/WebRTC и реальные устройства.

Утверждение baseline не означает завершение F16/M10, B08, нагрузочных тестов, production rollout или физических звонков. Эти работы остаются отдельными этапами.
