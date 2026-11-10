# chat-backend — ASP.NET Core Web API

Отдельный репозиторий **C# / ASP.NET Core** для chat. Все клиенты обращаются к одному API. Реализованы этапы B00–B05 для аккаунтов, верификации компаний, каталога, диалогов и доставки сообщений.

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

## Локальный запуск

Для публикации на Linux-сервере используйте отдельный
[production Compose и пошаговую инструкцию](docs/server-deployment.md). PostgreSQL работает
в контейнере, а API подключается к существующему Nginx на хосте. Миграции запускаются отдельным
контейнером после резервной копии. Локальный Compose ниже
сохраняет назначение для разработки.

Понадобятся .NET SDK 10.0.403 или новее в ветке .NET 10 согласно `global.json` и Docker Compose v2+ с запущенным Linux Docker Engine.
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
$env:Jwt__Issuer = 'chat-local'
$env:Jwt__Audience = 'chat-api'
$env:Jwt__SigningKeyBase64 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project src/chat.Api --launch-profile http
```

Во втором терминале проверьте оба маршрута.

```powershell
Invoke-RestMethod http://localhost:5105/health/live
Invoke-RestMethod http://localhost:5105/health/ready
```

`/health/live` проверяет процесс. `/health/ready` проверяет PostgreSQL, применённые миграции и обязательную конфигурацию. Он возвращает
HTTP 503 с REST-конвертом ошибки, пока API не готов принимать трафик. MinIO Console доступна на порту 9001,
Mailpit UI на 8025 и SMTP на 1025. Redis доступен для локальной разработки.
Для остановки выполните `docker compose --env-file .env -f deploy/docker-compose.yml down`.

Конфигурация ASP.NET Core переопределяется переменными окружения. `.env` читает Compose,
а API получает `ConnectionStrings__Postgres` из окружения. Для CORS задавайте точные origin
через `Cors__AllowedOrigins__0`, `Cors__AllowedOrigins__1` и далее. Лимит HTTP-запросов по IP
настраивается через `RateLimiting__PermitLimit` и `RateLimiting__WindowSeconds`.

Для входа и обновления сессии задайте `Jwt__Issuer`, `Jwt__Audience` и `Jwt__SigningKeyBase64`
через окружение или хранилище секретов. Ключ HS256 должен содержать не менее 32 случайных байт
в Base64. В локальной PowerShell-сессии можно сгенерировать отдельный ключ командой
`$env:Jwt__SigningKeyBase64 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))`.
Для HTTPS-разработки используйте `$env:Jwt__Issuer = 'chat-local'
$env:Jwt__Audience = 'chat-api'
$env:Jwt__SigningKeyBase64 = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet run --project src/chat.Api --launch-profile https`.
В развёрнутых средах используйте HTTPS/WSS и исключите query string из журналов прокси.

Для подтверждения email и сброса пароля API отправляет одноразовые коды через Mailpit по умолчанию.
Для SMTP укажите `AccountEmail__Host`, `AccountEmail__Port`, `AccountEmail__From` и
`AccountEmail__EnableTls=true` для удалённого сервера. Учётные данные задаются через хранилище
секретов. Письмо ставится в PostgreSQL в транзакции с изменением аккаунта. Для
`AccountEmail__QueueKeyBase64` используйте отдельный 32-байтовый случайный ключ Base64 из
хранилища секретов. В Development допускается ключ, производный от JWT. Для восстановления
очереди после перезапуска сохраняйте тот же ключ. Если письмо потерялось, повторите вход с
верным паролем для неподтверждённого аккаунта или запросите сброс снова через минуту.

Администратор платформы создаётся из доверенной консоли после миграции. Передайте email и пароль
через `AdminSeed__Email` и `AdminSeed__Password`, затем выполните
`dotnet run --project src/chat.Api --no-launch-profile -- --seed-admin`. Удалите переменные из сессии
после выполнения. CLI создаёт отдельную запись с уникальным email.

### Вложения и push

Запустите профиль `scanning` командой `docker compose --env-file .env -f deploy/docker-compose.yml --profile scanning up -d --wait`.
Для локальной разработки настройте `ObjectStorage__ServiceUrl=http://127.0.0.1:9000`,
`ObjectStorage__AccessKey` и `ObjectStorage__SecretKey` из локальных MinIO credentials,
`ObjectStorage__CreateBucket=true` и `FileScan__Host=127.0.0.1`. По умолчанию API использует
bucket `chat-media` и порт сканера `3310`. Для рабочего окружения укажите HTTPS-адрес S3, приватный
bucket с ограниченными правами и ClamAV в доверенной сети. Правило удаления из `incoming/`
после суток включайте после согласования retention. Оно дополняет уборку по метаданным и
должно учитывать legal hold. Для `media/` запретите безусловное удаление по возрасту.
Временный сбой сканера оставляет документы и вложения PENDING.
CORS для браузерного PUT/GET
настройте отдельно в объектном хранилище.

SignalR доставляет сохранённые события из PostgreSQL Outbox подключённым участникам. Текущая
реализация допускает один Production API на PostgreSQL БД. Второй процесс отклоняется при запуске,
а потеря блокировки останавливает API. После reconnect клиент заново подписывается
и догружает историю через REST. Квитанция прочтения отправляется через REST.

Для Expo push задайте `Push__Enabled=true` и, если у проекта включена защита Expo,
`Push__AccessToken` через хранилище секретов. Worker сохраняет билет Expo, повторяет временные
ошибки и проверяет итоговую квитанцию. Payload содержит общие уведомления и идентификаторы
события и диалога.

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
`artifacts/openapi.json`. После согласованного изменения контракта обновите версию.
Для воспроизводимого экспорта из корня репозитория используйте SDK из `global.json`
и версии зависимостей из репозитория.

```powershell
$env:CHAT_OPENAPI_EXPORT = Join-Path (Get-Location) 'artifacts/openapi.json'
try {
    dotnet test tests/chat.ContractTests
    if ($LASTEXITCODE) { throw 'OpenAPI export failed' }
    $first = (Get-FileHash $env:CHAT_OPENAPI_EXPORT -Algorithm SHA256).Hash
    dotnet test tests/chat.ContractTests --no-build
    if ($LASTEXITCODE) { throw 'OpenAPI re-export failed' }
    if ($first -ne (Get-FileHash $env:CHAT_OPENAPI_EXPORT -Algorithm SHA256).Hash) {
        throw 'OpenAPI export changed between runs'
    }
} finally {
    Remove-Item Env:CHAT_OPENAPI_EXPORT
}
npx --yes @redocly/cli@2.76.0 lint --extends spec artifacts/openapi.json
```

Версия приложения закреплена в `Directory.Build.props`. REST-контракт версии 0.5.0
опубликован в OpenAPI, контракт SignalR версионируется отдельно в `docs/contracts/events-v1.md`.
Redocly проверяет структуру документа и ссылки. Совпадение ответов сервера проверяют тесты.
Обновления потребителей оформляются отдельными PR по [матрице готовности](docs/hardening/B01-B05-release-readiness.md).

## Чек-лист миграции

1. Создайте миграцию и snapshot командой
   `dotnet ef migrations add <Name> --project src/chat.Infrastructure --startup-project src/chat.Api --output-dir Persistence/Migrations`.
2. Проверьте изменения схемы, индексы, блокировки, сохранность данных и план отката.
   Просмотрите SQL из `dotnet ef migrations script --idempotent --project src/chat.Infrastructure --startup-project src/chat.Api`.
3. Проверьте миграцию на чистой и существующей PostgreSQL. Выполните тесты
   и `dotnet ef migrations has-pending-model-changes --project src/chat.Infrastructure --startup-project src/chat.Api`.
4. Перед развёртыванием проверьте резервную копию, примените согласованный SQL отдельным шагом
   и проверьте readiness. Старт API не запускает миграции автоматически.

## Production за Nginx

Используйте поддерживаемый патч .NET 10. Минимальная версия для API в B05.1 — `10.0.14`.
Проверяйте `dotnet --list-runtimes` в том же контейнере или под той же службой, где работает API.
`Microsoft.AspNetCore.App` и `Microsoft.NETCore.App` должны быть на утверждённом актуальном патче.
`global.json` фиксирует минимальный SDK `10.0.403`, а `chat.Api.csproj` задаёт минимальный runtime.
Аудит NuGet не проверяет установленный shared runtime.

1. Возьмите за основу [конфигурацию Nginx](deploy/nginx/chat-backend.conf). Замените публичное имя
   и подключите сертификат edge. Настройте отдельный сертификат Kestrel с SAN `chat-backend.internal`
   либо согласуйте другое имя в `proxy_ssl_name`. Передайте Nginx CA для проверки сертификата Kestrel.
2. На одном хосте привяжите Kestrel к `https://127.0.0.1:5105`. Передайте сертификат через
   `Kestrel__Certificates__Default__Path` и пароль через secret manager. При разных хостах
   используйте приватную сеть и firewall, разрешающий backend-порт только Nginx. Публичный DNS
   и входящий трафик должны указывать на edge.
3. Задайте `ReverseProxy__KnownProxies__0=127.0.0.1` для IPv4 loopback или точный адрес Nginx,
   который видит Kestrel. Укажите публичное имя API в `AllowedHosts`. Nginx перезаписывает
   `X-Forwarded-For` и `X-Forwarded-Proto`. Автоматический режим `ASPNETCORE_FORWARDEDHEADERS_ENABLED`
   отключён в Production. Приложение проверяет реальный TLS и IP прокси до обработки заголовков.
4. Выставьте ровно одну Production-реплику и один upstream в Nginx. Используйте stop-first rollout
   к одному writable primary PostgreSQL. Для lock-сессии требуется прямой PostgreSQL или пулер
   в режиме session pooling. Примените EF-миграции перед запуском. Передайте конфигурацию JWT, HTTPS S3,
   `FileScan__Host`, `AccountEmail__Host` и отдельный ключ `AccountEmail__QueueKeyBase64`.
   Проверьте `/health/ready` через HTTPS edge.
   Проверка готовности подтверждает схему и наличие настроек. Доступность S3, SMTP и ClamAV
   проверяйте отдельными эксплуатационными probes.
5. На целевой инфраструктуре проверьте отказ HTTP, HTTPS login, WSS handshake через edge,
   отсутствие публичного доступа к backend-порту и исключение query string из журналов.

Для ClamAV используйте актуальные сигнатуры и доверенную сеть. Настройте отклонение зашифрованных
файлов и файлов, превысивших лимиты анализа, как сканируемые угрозы через параметры clamd
`AlertEncrypted` и `AlertExceedsMax`. Верификационные документы, включая созданные до B05.1,
сканируются в фоне. До результата READY скачать их и одобрить заявку нельзя. Временный сбой
сканера оставляет документ PENDING для повторной попытки. [Настройки clamd](https://github.com/Cisco-Talos/clamav/blob/main/etc/clamd.conf.sample).

Пороги алертов, диагностика очередей и действия при отказах описаны в
[эксплуатационном отчёте B05.3](docs/hardening/B05.3-results.md).
