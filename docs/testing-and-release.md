# Backend: тестирование, CI/CD, релизы (.NET)

## Окружения

`local` (Docker Compose: PostgreSQL, Redis, S3-compatible test storage, mail catcher), `staging`, `production`. Секреты только из secret manager/окружения, `appsettings.Development.json` без реальных паролей. Отдельные DB, бакеты и TURN credentials для каждой среды.

## CI quality gates

1. `dotnet restore` (locked restore после появления `packages.lock.json`, если включён Central Package Management).
2. `dotnet format <solution> --verify-no-changes`.
3. `dotnet build <solution> --no-restore -warnaserror`.
4. `dotnet test <solution> --no-build` — unit + WebApplicationFactory integration, реальные PostgreSQL via Testcontainers.
5. EF Core migration drift/apply smoke against ephemeral PostgreSQL; OpenAPI publish + breaking-change diff + SignalR contract tests.
6. Dependency vulnerability / secret / SAST scan; PR блокируется если качество не проходит.

## Матрица приёмки Этапа 1

- Клиент регистрируется, подтверждает email, входит и видит только verified businesses.
- Веб-заявка владельца Pending не доступна в каталоге; PLATFORM_ADMIN Approve открывает права staff web/mobile.
- Клиент инициирует единственный B2C chat; отправляет текст/фото/файл; сотрудник отвечает из web и mobile.
- Чужой клиент или сотрудник другой компании не может читать/отправлять сообщения, войти в чужую группу SignalR или скачать вложение.
- `clientMessageId` исключает дубли при retry; после disconnect история догружается REST.
- Rejected/Suspended скрывает компанию и блокирует бизнес-действия; role revoke отключает realtime.
- Все push payloads без текста/секретов, чувствительные данные не появляются в logs.

## Этап 2

Проверка типов аудио/видео, 1:1 WebRTC, ICE/TURN, reject/busy/missed/hangup, state machine, permissions, физические устройства; нет записи звонков.

## Этап 3

Приглашение/отзыв оператора, assignment, аналитика, роли; все функции остаются бесплатными.

## Production

TLS и WSS, health probes, миграции с roll-forward/backout plan, резервное копирование и restore test, outbox lag, ошибки SignalR, WebRTC TURN traffic, rate limiting/abuse alerts, security/privacy review до production.
