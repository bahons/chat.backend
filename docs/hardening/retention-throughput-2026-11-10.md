# Масштабирование очистки очередей — 10 ноября 2026

Первый этап после аудита: устранено ограничение DB retention в 200 строк за 10 минут.
REST и SignalR не изменены. Production guard одной реплики остаётся обязательным:
общая маршрутизация realtime, распределённый отзыв доступа и лимитер ещё не реализованы.

## Поведение

`RetentionWorker` начинает проход сразу после запуска, затем ждёт 30 секунд после окончания
прохода. По умолчанию выполняет до 50 партий с мягким бюджетом 5 секунд. Каждая партия
удаляет максимум по 200 устаревших строк refresh/Outbox/push/mail и отзывает до 200 устройств.
Если хотя бы один тип заполнил партию, начинается следующая; иначе проход заканчивается.
Новая партия не начинается после исчерпания бюджета. Начатая партия может превысить его
на время запросов БД. Параллельных sweep внутри worker нет, ошибка не отменяет уже
завершённые удаления и оставляет хвост следующему проходу.

Настройки под `Retention`, с проверкой на старте:

| Настройка | По умолчанию | Диапазон |
| --- | --- | --- |
| SweepIntervalSeconds | 30 | 1–600 |
| MaxBatchesPerSweep | 50 | 1–1000 |
| SweepBudgetSeconds | 5 | 1–60 |

`Approved` по-прежнему false. Сроки хранения и production hold не изменены и не включались.
Approval/hold перечитываются перед каждой партией из пяти типов данных. Hold не отменяет
уже выполняемую партию. Для немедленной остановки следует остановить cleanup worker.
Pending-записи, канонические сообщения и документы модерации эта очистка не удаляет.

## Индексы и развёртывание

Миграция `RetentionThroughput` добавляет `(ExpiresAt, Id)` для RefreshSessions и частичные
`(CompletedAt, Id) WHERE CompletedAt IS NOT NULL` для трёх очередей. Рабочие индексы
pending-очередей сохранены. Down удаляет только четыре новых индекса.

Применять миграцию отдельным шагом до запуска новой версии API, с резервной копией и
согласованным окном обслуживания: обычный CREATE INDEX блокирует запись в таблицу на время
построения. Миграция остаётся транзакционной и совместимой с idempotent SQL. Для крупной
живой БД подготовить отдельный проверенный online rollout; не заменять автоматически
индексы на CONCURRENTLY внутри транзакционного скрипта.
[Ограничения PostgreSQL CREATE INDEX](https://www.postgresql.org/docs/17/sql-createindex.html).

## Измерения

Meter `chat.Workers`, версия 1.0.0:

| Инструмент | Тип | Смысл |
| --- | --- | --- |
| chat.retention.affected | Counter, rows | Удалённые строки; для queue=devices — отозванные устройства |
| chat.retention.duration | Histogram, seconds | Длительность прохода, включая ошибочные и остановленные hold |
| chat.queue.overdue.capped | Histogram, rows | Сэмплы числа просроченных записей, ограниченные 1001; включает нулевые сэмплы |

Единственный тег счётчиков/очередей — `queue` с фиксированными именами, без ID, текстов
сообщений и токенов. Экспортёр и публичный metrics endpoint не добавлены: подключить meter
к принятому в инфраструктуре .NET metrics/OpenTelemetry collector. Существующие backlog-логи
233/234 и результаты cleanup 236/237 сохранены. Это начало наблюдаемости; измерения
oldest age, dispatch latency, DB pool wait и SLO ещё требуются.

## Проверки

Новые интеграционные сценарии используют PostgreSQL 17: обработка более одной партии,
ограничение числа партий, legal hold между партиями и возобновление, сохранение pending,
запрет начала новой партии после истечения монотонного бюджета. Модель и SQL миграции
проверяются EF; существующий migration smoke проходит upgrade/downgrade и исторические данные.

Команды из корня `chat-backend` выполнялись SDK 10.0.403 из
`D:\project\chat\.local-tools\dotnet` с локальным `DOTNET_CLI_HOME`:

| Команда | Результат |
| --- | --- |
| `dotnet restore chat.sln --packages D:\project\chat\.local-tools\nuget -p:NuGetAuditMode=all -warnaserror:NU1900,NU1903,NU1904,NU1905` | PASS |
| `dotnet build chat.sln --no-restore -warnaserror` | PASS, 0 ошибок и предупреждений |
| `dotnet format chat.sln --no-restore --verify-no-changes` | PASS |
| `dotnet test chat.sln --no-build` | PASS, 51/51: 8 unit, 42 integration, 1 contract; 0 пропущено. PostgreSQL 17, включая migration smoke и OpenAPI snapshot |
| `dotnet ef migrations has-pending-model-changes --project src/chat.Infrastructure --startup-project src/chat.Api --no-build` | PASS |
| `dotnet ef migrations script --idempotent --project src/chat.Infrastructure --startup-project src/chat.Api --no-build --output D:\project\chat\.local-tools\retention-migrations.sql` | PASS, SQL просмотрен |
| `git diff --check` | PASS |

Изменены RetentionWorker, Program (валидация настроек), WorkConfiguration,
IdentityConfiguration, WorkerBacklogMonitor, добавлены WorkerMetrics и EF-миграция со
snapshot. Тесты — ScalingTests и изоляция ручного worker в IntegrityTests; документация —
architecture, data-model и отчёты аудита. NuGet и Docker проверялись с разрешённым доступом
вне песочницы. Промежуточные прогоны обнаружили гонки фоновой очистки с ручными тестами
и оставшиеся записи предыдущего сценария; тестовый стенд изолирован.

Нагрузочный soak не выполнялся; способность догонять произвольный входящий поток пока
не утверждается. Новая миграция проверяется на временных БД; production не менялся.
