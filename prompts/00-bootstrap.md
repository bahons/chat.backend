# Codex B00 — Bootstrap C# / ASP.NET Core [Этап 1]

Ты senior .NET engineer. Работаешь исключительно в `chat-backend`. Прочитай `AGENTS.md`, `README.md`, `docs/architecture.md`, `docs/decisions-and-risks.md`, оба contracts. Сейчас могут быть только Markdown-файлы.

## Реализуй

1. `.NET LTS` solution (`.slnx` или `.sln` по SDK), `global.json`, `Directory.Build.props`, `src/{Api,Application,Domain,Infrastructure,Contracts}` и `tests/{UnitTests,IntegrationTests,ContractTests}` по README. Включи nullable, analyzers, warnings as errors.
2. ASP.NET Core Web API, DI, appsettings + validated options, environment config, безопасный structured logging и `X-Request-Id`, единый error mapper в JSON формате `docs/contracts/api-v1.md`.
3. EF Core + Npgsql `ChatDbContext`, migrations framework; инфраструктура `deploy/docker-compose.yml` для PostgreSQL, Redis, MinIO и mail catcher. Пока без доменных таблиц, если не нужны для bootstrap.
4. ASP.NET Core authentication/authorization policies scaffold, rate limiting, `health/live` и `health/ready`, CORS allowlist (не механизм security авторизации).
5. ASP.NET Core SignalR hub `/hubs/messaging` (только security/connect scaffolding на B00); никакого Socket.IO. OpenAPI generation/export в `artifacts/openapi.json` при наличии endpoints.
6. xUnit, `WebApplicationFactory` и Testcontainers for PostgreSQL, CI (GitHub Actions или нейтральный provider), `.env.example` без секретов.
7. Версионирование, migration checklist, простые инструкции `dotnet run`, `dotnet test`, `docker compose up` в README.

## Критерии

`dotnet restore`, `dotnet format --verify-no-changes`, `dotnet build -warnaserror`, `dotnet test` проходят; local infra запускается, health endpoints работают. Не создавать placeholder endpoints с фальшивыми данными и не утверждать зелёный тест без реального запуска.

## Отчёт

Перечисли файловую структуру, точные команды/результаты, инфраструктурные блокеры. Запрещено устанавливать NestJS, Prisma, Node.js сервер и Socket.IO.
