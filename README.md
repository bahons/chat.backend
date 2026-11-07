# chat-backend — ASP.NET Core Web API

Отдельный репозиторий **C# / ASP.NET Core** для chat. Все клиенты обращаются к одному API. Версия B00 поднимает API, PostgreSQL и инфраструктуру для следующих этапов.

## Начало работы

Codex: `AGENTS.md` → `docs/architecture.md` → `docs/data-model.md` → `docs/security.md` → `docs/contracts/*` → `prompts/00-bootstrap.md`. Затем выполнять B01…B08 по порядку.

## Стек

- C# с `nullable enable`, `ImplicitUsings`, строгими анализаторами; актуальный поддерживаемый **.NET LTS** (фиксировать в `global.json` и CI).
- ASP.NET Core Web API с Controllers или Minimal APIs по единому стилю; не смешивать стили хаотично. **ASP.NET Core SignalR** для realtime.
- **Entity Framework Core + Npgsql + PostgreSQL**; миграции EF Core, транзакции, оптимистическая конкуренция где требуется.
- ASP.NET Core authentication/authorization, JWT access token, refresh rotation, policy-based business ACL.
- Redis (rate limits/ephemeral presence/coordination), S3-compatible private object storage, BackgroundService/worker + transactional Outbox.
- Swashbuckle или встроенный OpenAPI pipeline, xUnit + ASP.NET Core `WebApplicationFactory` + Testcontainers PostgreSQL, Docker Compose для local dependencies.
- STUN/TURN для WebRTC 1:1 на Этапе 2 (без пересылки медиапотока через API).

## Целевая файловая архитектура

```text
chat-backend/
├─ chat.slnx                 # .sln, если версия toolchain не поддерживает slnx
├─ global.json
├─ Directory.Build.props
├─ src/
│  ├─ chat.Api/
│  │  ├─ Program.cs
│  │  ├─ Endpoints/                   # или Controllers/; REST v1, health
│  │  ├─ Hubs/                        # MessageHub и позднее call signaling
│  │  ├─ Middleware/                  # request id, exception mapping
│  │  └─ DependencyInjection/
│  ├─ chat.Application/
│  │  ├─ Auth/ Businesses/ Verification/ Directory/
│  │  ├─ Conversations/ Messages/ Media/ Notifications/
│  │  └─ Calls/ Team/ Analytics/     # поздние этапы
│  ├─ chat.Domain/
│  │  ├─ Entities/ ValueObjects/ Enums/ Events/ Policies/
│  │  └─ Exceptions/
│  ├─ chat.Infrastructure/
│  │  ├─ Persistence/                 # DbContext, EF configurations, migrations
│  │  ├─ Identity/ Redis/ Storage/ Realtime/
│  │  └─ Outbox/ Workers/ Email/ Push/
│  └─ chat.Contracts/        # публичные REST DTO/event envelopes; без доменных сущностей
├─ tests/
│  ├─ chat.UnitTests/
│  ├─ chat.IntegrationTests/
│  └─ chat.ContractTests/
├─ docs/
│  ├─ contracts/api-v1.md
│  ├─ contracts/events-v1.md
│  └─ ...
├─ prompts/
├─ deploy/docker-compose.yml         # local: PostgreSQL, Redis, MinIO, mail-catcher
├─ .env.example
└─ AGENTS.md
```

Вместо прежних TypeScript/NestJS/Prisma/Socket.IO решений применять только ASP.NET Core/EF Core/SignalR. **Не создавать пустые заглушки бизнес-логики ради зелёного CI.**

## Последовательность

- B00 foundation → B01 auth → B02 веб-заявки/верификация → B03 каталог → B04 чаты → B05 SignalR/media/push (**Этап 1**).
- B06 media + WebRTC signaling (**Этап 2**).
- B07 team/analytics/integrations (**Этап 3**), B08 hardening/release.

**Все функции бесплатны при запуске.** Монетизация описана только в `docs/future-monetization.md`.

## Локальный запуск B00

Понадобятся .NET 10 SDK из `global.json` и Docker Compose v2+ с запущенным Linux Docker Engine.
Команды ниже выполняются в корне chat-backend в PowerShell.
При первом запуске Compose собирает MinIO из закреплённого релиза исходного кода.

```powershell
Copy-Item .env.example .env
# Заполните POSTGRES_PASSWORD и MINIO_ROOT_PASSWORD локальными паролями.
# Если Windows резервирует порт 5432, задайте POSTGRES_PORT=15432 в .env.
docker compose --env-file .env -f deploy/docker-compose.yml up -d --wait
$postgresPort = ((Select-String -LiteralPath .env -Pattern '^POSTGRES_PORT=').Line -split '=', 2)[1]
$postgresPassword = Read-Host 'POSTGRES_PASSWORD из .env'
$env:ConnectionStrings__Postgres = "Host=127.0.0.1;Port=$postgresPort;Database=chat;Username=chat;Password=$postgresPassword;Timeout=3;Command Timeout=3"
dotnet restore
dotnet tool restore
dotnet ef database update --project src/chat.Infrastructure --startup-project src/chat.Api
dotnet run --project src/chat.Api --launch-profile http
```

Во втором терминале проверьте оба маршрута.

```powershell
Invoke-RestMethod http://localhost:5105/health/live
Invoke-RestMethod http://localhost:5105/health/ready
```

`/health/live` проверяет процесс. `/health/ready` проверяет подключение к PostgreSQL и возвращает
HTTP 503 с REST-конвертом ошибки, если БД недоступна. Redis, MinIO и Mailpit подключаются к API
на последующих этапах. MinIO Console доступна на порту 9001, Mailpit UI на 8025 и SMTP на 1025.
Для остановки выполните `docker compose --env-file .env -f deploy/docker-compose.yml down`.

Конфигурация ASP.NET Core переопределяется переменными окружения. `.env` читает Compose,
а API получает `ConnectionStrings__Postgres` из окружения. Для CORS задавайте точные origin
через `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1` и далее. Лимит HTTP-запросов по IP
настраивается через `RateLimiting__PermitLimit` и `RateLimiting__WindowSeconds`.

Hub `/hubs/messaging` требует подписанный JWT с `sub`. Для проверки внешних тестовых токенов
задайте `Jwt__Issuer`, `Jwt__Audience` и `Jwt__SigningKeyBase64` через окружение или хранилище
секретов. Ключ HS256 должен содержать не менее 32 байт случайных данных. Без ключа Hub закрыт.
Для HTTPS-разработки используйте `dotnet run --project src/chat.Api --launch-profile https`.
Выдача токенов и проверка отзыва сессий относятся к B01. Проверки membership,
доступ к группам и доставка событий появятся вместе с функциями обмена сообщениями.
В развёрнутых средах используйте HTTPS/WSS и исключите query string из журналов прокси.

## Проверки и OpenAPI

```powershell
dotnet restore
dotnet format chat.sln --no-restore --verify-no-changes
dotnet build chat.sln --no-restore -warnaserror
dotnet test chat.sln --no-build
```

Полный прогон использует Docker для временного PostgreSQL. Для прогона без Docker
используйте `dotnet test chat.sln --no-build --filter 'Dependency!=Docker'`.

В Development схема доступна по `/openapi/v1.json`. Contract test сравнивает её с
`artifacts/openapi.json`. После согласованного изменения контракта обновите версию
и экспортируйте схему из текущего кода:

```powershell
$env:CHAT_OPENAPI_EXPORT = Join-Path (Get-Location) 'artifacts/openapi.json'
try { dotnet test tests/chat.ContractTests } finally { Remove-Item Env:CHAT_OPENAPI_EXPORT }
```

Версия приложения закреплена в `Directory.Build.props`. REST-контракт версии 0.1.0
опубликован в OpenAPI, контракт SignalR версионируется отдельно в `docs/contracts/events-v1.md`.
При несовместимых изменениях обновите контракт и согласуйте изменения с обоими потребителями.

## Чек-лист миграции

1. Создайте миграцию и snapshot командой
   `dotnet ef migrations add <Name> --project src/chat.Infrastructure --startup-project src/chat.Api --output-dir Persistence/Migrations`.
2. Проверьте изменения схемы, индексы, блокировки, сохранность данных и план отката.
   Просмотрите SQL из `dotnet ef migrations script --idempotent --project src/chat.Infrastructure --startup-project src/chat.Api`.
3. Проверьте миграцию на чистой и существующей PostgreSQL. Выполните тесты
   и `dotnet ef migrations has-pending-model-changes --project src/chat.Infrastructure --startup-project src/chat.Api`.
4. Перед развёртыванием проверьте резервную копию, примените согласованный SQL отдельным шагом
   и проверьте readiness. Старт API не запускает миграции автоматически.
