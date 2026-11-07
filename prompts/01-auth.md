# Codex B01 — Identity, refresh sessions и роли [Этап 1]

Прочитай `AGENTS.md`, `docs/security.md`, `docs/data-model.md`, `docs/contracts/api-v1.md`; требуется B00.

## Реализуй

1. EF Core entities/configurations/migrations: `User`, `RefreshSession`, `BusinessMembership` (при необходимости связь с `Business` на B02); enum `CLIENT`, `BUSINESS_MEMBER`, `PLATFORM_ADMIN`. UUID PK, unique normalized email.
2. ASP.NET Core AuthN/AuthZ: JWT access token, refresh rotation (hashed tokens at rest, replay detection, revoke chain); policy-based access checks, `ClaimsPrincipal` mapping без повышения прав на стороне клиента.
3. `POST /v1/auth/client/register`, login, refresh, logout, email verify, forgot/reset и `GET /v1/me` строго по контракту.
4. Password hashing: Argon2id (надёжная проверенная .NET библиотека) либо ASP.NET Core Identity `PasswordHasher` с документированным обоснованием и параметрами; защита reset/login от abuse и user enumeration.
5. Email adapter local + production interface; проверка токенов, запрет публичной регистрации администратора; secure admin seeding/CLI.
6. DTO validation, centralized errors, OpenAPI schemas, xUnit + WebApplicationFactory integration tests с PostgreSQL на enum/role escalation, refresh replay, CSRF/cookie behavior при необходимости.

## Критерии

Клиент может пройти auth; нельзя получить BUSINESS_MEMBER/PLATFORM_ADMIN через client registration; refresh после rotation invalid; security tests и `dotnet test` проходят.
