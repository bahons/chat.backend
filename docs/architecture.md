# Backend: архитектура C# / ASP.NET Core

## Назначение

Единый ASP.NET Core Web API обслуживает веб-платформу (компании + модераторы) и мобильное приложение (клиенты + сотрудники компаний). Только веб содержит регистрацию компаний; сотрудники используют web/mobile после ручной верификации.

## Модульный монолит

- **API:** Controllers или Minimal API endpoints (выбрать единый стиль на B00), middleware, auth policies, health, OpenAPI, SignalR Hubs.
- **Application:** use cases, commands/queries, DTO mapping, permissions orchestration, transaction boundaries, outbox dispatch triggers.
- **Domain:** User, Business, Application, Conversation, Message, Role/Status, правила переходов. Без зависимостей на EF Core и ASP.NET Core.
- **Infrastructure:** `ChatDbContext`, `IEntityTypeConfiguration<T>`, EF Core migrations, Npgsql, Redis, S3 storage, mail/push adapters, background workers, Outbox.
- **Contracts:** стабильные REST DTO и envelope событий; consumer repos используют сгенерированные независимые клиенты, не импортируют этот проект.

## Модули (bounded contexts)

1. Auth/users: identity, password hashing, email confirmation/reset, access JWT, refresh rotation.
2. Businesses/verification: владельцы, веб-заявки, защищённые документы, ручные решения, suspension.
3. Directory: поиск/публичный профиль только verified-active business.
4. Conversations/messages: B2C-only, идемпотентность `clientMessageId`, cursor pagination, receipts.
5. Media: S3-compatible private buckets, validate/scan/quarantine, short-lived authorized downloads.
6. Realtime: ASP.NET Core SignalR (`/hubs/messaging`), авторизация на подключении и при методах, серверные группы, события после DB commit.
7. Notifications: push/email, Outbox → resilient worker with retries.
8. Calls: Этап 2 — WebRTC signaling через SignalR и STUN/TURN; не media relay backend.
9. Team/analytics: Этап 3.

## Данные

- **PostgreSQL + EF Core**: authoritative state. `DbContext` scoped per request/worker task; не использовать общий контекст в singleton / SignalR hub.
- **Redis**: временные presence/cache/rate-limits/coordination. Не хранить canonical сообщения.
- **Object storage**: отдельные приватные пути/политики для media и verification docs.
- **Outbox**: сохранять сообщение и событие в одной DB-транзакции; отдельный worker публикует и повторяет при сбоях. Потребители дедуплицируют events.
- **SignalR scale-out**: согласовать Redis backplane или управляемый SignalR при нескольких инстансах; sticky sessions/transport настройки тестировать, WebSockets через WSS.
- **Стартовое ограничение B05.3**: один активный Production API и один Outbox dispatcher на PostgreSQL БД. Старт защищён session advisory lock. События для отсутствующих подписчиков помечаются `offline`, восстанавливаемое состояние остаётся доступным через REST. Outbox, Expo и SMTP используют короткие DB-аренды, затем выполняют внешнее I/O вне транзакции. [Семантика и runbook](hardening/B05.3-results.md)
- **Проверка масштабирования 10.11.2026**: создание диалога использует совместимую блокировку компании и conflict-safe INSERT; Outbox обрабатывает доступную очередь без квоты на секундный проход. Горизонтальный запуск требует общего протокола доставки и отзыва доступа; отдельно необходимо увеличить пропускную способность retention. [Результаты, изменения и ограничения](hardening/scalability-review-2026-11-10.md)

## Сценарии

Очистка завершённых очередей обрабатывает несколько ограниченных партий за проход,
с бюджетом времени и проверкой legal hold перед каждой партией. Индексы и метрики описаны
в [результатах этапа retention](hardening/retention-throughput-2026-11-10.md).

**Бизнес:** web application → `PENDING_VERIFICATION` → ручной approve/reject → `VERIFIED` → появляется в каталоге → owner работает в web/mobile.

**Чат:** клиент ищет VERIFIED компанию → `POST /v1/conversations` → `POST /v1/conversations/{id}/messages` с idempotency → EF Core transaction → Outbox → SignalR `message.created` → REST reconciliation после reconnect.

**Блокировка бизнеса:** админ снимает публичность, запрещает бизнес-операции по policy, рассылает revoke event / инвалидирует membership, сохраняет данные по retention policy.

## Открытые решения

- E2EE **не утверждено**. На старте TLS + encryption at rest + server-side ACL; нельзя рекламировать E2EE до внедрения.
- Доменная юрисдикция и требования к персональным данным до production.
- .NET LTS зафиксировать в `global.json`. Начальное web UI — ASP.NET Core Blazor Web App, но frontend repo независим.
