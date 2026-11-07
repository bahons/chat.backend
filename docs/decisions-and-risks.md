# Backend: ADR, принятые решения и риски

## Решения владельца продукта

- B2C-only messenger; поиск только компаний; регистрация/верификация компаний в веб-сценарии.
- После верификации компания использует **веб и iOS/Android**.
- Три согласованных этапа реализации.
- Сейчас всё бесплатно, будущие тарифы лишь в ТЗ.
- **Backend на C# / ASP.NET Core**, не NestJS.
- **Веб-платформа на ASP.NET Core** (UI-технология Blazor — пока рабочая инженерная гипотеза).

## Технические ADR: текущий baseline

| ID | Решение | Причина / оговорки |
|---|---|---|
| ADR-01 | C# + ASP.NET Core Web API, модульный монолит | соответствует стеку владельца; простой деплой и контролируемые bounded contexts |
| ADR-02 | PostgreSQL + Entity Framework Core (Npgsql) | транзакции, migrations, .NET ecosystem |
| ADR-03 | ASP.NET Core SignalR, Hub contract v1 | realtime для .NET веба + JS/RN клиента; **не** Socket.IO |
| ADR-04 | Клиент email/password с verify email | рабочее допущение; phone OTP ещё не утверждён |
| ADR-05 | Один диалог на `(clientUserId,businessId)` | MVP простой inbox; обращения-треды — отдельный RFC |
| ADR-06 | Клиент первым создаёт диалог | защита от unsolicited marketing |
| ADR-07 | TLS + encryption at rest; без заявления E2EE | многопользовательские бизнес-аккаунты требуют отдельного решения |
| ADR-08 | Одно mobile app с двумя ролями | рабочее допущение до финального UX решения |
| ADR-09 | Web: ASP.NET Core Blazor Web App Interactive Server | рабочий вариант; Razor Pages/MVC возможны до F00 |
| ADR-10 | Mobile: React Native + Expo | не утверждено владельцем отдельно; .NET MAUI возможно по отдельному решению |

## Риски

Фальшивые заявки; spam/abuse; стоимость media и TURN; юридические ограничения на обработку данных; масштабирование EF Core индексов, SignalR scale-out; offline/reconnect; кража сессий; злоупотребления сотрудниками/конкуренция при назначении операторов.

## Изменение

В `docs/adr/NNN-topic.md` фиксировать Context/Decision/Consequences/Rollback; обновлять REST/OpenAPI, SignalR контракт и потребительские репозитории согласованными PR. Codex не имеет права молча подменять ASP.NET Core другим серверным стеком.
