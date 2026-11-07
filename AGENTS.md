# Инструкции Codex — backend (C# ASP.NET Core)

> **Именование:** внутреннее имя проекта — `chat` (строчные буквы), Git-репозиторий — `chat-backend`. Для .NET использовать префикс проектов/пространств имён `chat.` (Api, Application, Domain, Infrastructure, Contracts); не заменять его другими названиями.

Сначала прочитай `README.md`, соответствующие `docs/*.md` и `docs/contracts/*.md`. Работаем **только в этом Git-репозитории**. Не создавай код `frontend` или `mobile` здесь и не делай relative imports между репозиториями.

## Неприкосновенные правила проекта

1. Диалоги только `CLIENT ↔ BUSINESS`; ровно один clientUserId и один businessId. Никаких client-client, business-business, несанкционированных group chats.
2. Поиск/карточки доступны только для компаний с `VERIFIED && isActive`.
3. Регистрация бизнеса только через официальный веб-сценарий; выдача полномочий после ручной модерации платформенным администратором. Запрос HTTP невозможно достоверно распознать как «из веба» по Origin/User-Agent; безопасность — в проверке заявки.
4. Проверяй business memberships / роли **для каждого** API endpoint и SignalR Hub метода, включая подключение к группе и рассылку события. После suspension / role revoke доступ пересматривается, подключения завершаются.
5. Диалог инициирует клиент; сотрудники только отвечают. Нет рекламы/рассылок.
6. Весь стартовый функционал бесплатен; **никакого биллинга, paywall, тарифных ACL и платёжных SDK**. `docs/future-monetization.md` — только future design.
7. Истина REST — OpenAPI из backend; события — `docs/contracts/events-v1.md` для ASP.NET Core **SignalR**. Изменение контракта → версия/документация/отдельные PR в обоих потребителях.
8. Собственный визуальный бренд: не копировать защищённые ресурсы/брендинг WhatsApp.

## Стиль C# и архитектура

- ASP.NET Core Web API, EF Core + Npgsql, PostgreSQL, SignalR, BackgroundService, Outbox; соблюдать `global.json`, `nullable`, analyzers, async CancellationToken.
- Модульный монолит, отдельные Domain/Application/Infrastructure/Api/Contracts. Не выделять микросервисы без реальной потребности.
- Для auth использовать встроенные auth/authorization primitives, политики и явные проверки владения. Не доверять claims без актуального membership check.
- EF Core configurations + migrations, транзакции для пересечения бизнес-состояний, уникальные индексы, consistency.
- DTO validation (например FluentValidation или ASP.NET Core validation), централизованная схема ошибок v1, security logging без PII/токенов/SDP.
- Любые SQL/raw queries — параметризованные, любые storage URLs — короткоживущие.
- Тесты не подменять mocks критических security checks; integration tests с настоящим PostgreSQL.

## Рабочий цикл каждой задачи

Краткий план → изменения кода/миграций/тестов → проверки → список файлов и команд → оставшиеся проблемы. Не утверждай PASS для реально не запускавшихся тестов.

## Quality gate

`dotnet restore`, `dotnet format chat.slnx --verify-no-changes` (либо `.sln`), `dotnet build --no-restore`, `dotnet test --no-build`; `dotnet ef migrations`/DB smoke, backend contract check. Docker Compose только для локальной инфраструктуры.

## Очерёдность

`prompts/00-bootstrap.md` → B01 → B02 → B03 → B04 → B05 (**Этап 1**) → B06 (**Этап 2**) → B07 (**Этап 3**) → B08 release.
