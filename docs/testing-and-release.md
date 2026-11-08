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

Workflow `Backend / verify` должен быть обязательным статусом защищённой ветки. В PR нет
production credentials. Gitleaks проверяет файлы и достижимую историю с маскированием.
NuGet Audit охватывает транзитивные зависимости. Redocly проверяет структуру OpenAPI, а oasdiff
сравнивает схемы с сохранённым baseline 0.4.0 и base ref PR. Изменения поведения вроде порядка
inbox или смены владельца устройства требуют проверки тестами и consumer PR.

Semgrep проверяет исходники C# и выводит только правило, файл и строку. Проверка допускает
известные предупреждения парсера внутри сигнатур primary constructors и двух деклараций
`Program.cs`. Другие ошибки разбора, находки и недоступная конфигурация останавливают job.
Trivy проверяет HIGH/CRITICAL
в образе, построенном из publish output, и блокирует EOL. Проверку переносите на фактические
digests API, Nginx, PostgreSQL, S3 и ClamAV в целевом контуре. Отдельно сверяйте .NET runtime
с официальными security releases. NuGet и Trivy зависят от полноты своих баз, а SAST не
доказывает корректность ACL. Исключения требуют идентификатора, срока и ответственного.

### Локальный JS SignalR smoke

Подготовьте одноразовый тестовый стенд с HTTPS edge, действующей сессией CLIENT, её
диалогом и отдельным чужим диалогом. Передайте переменные `CHAT_SMOKE_API`,
`CHAT_SMOKE_TOKEN`, `CHAT_SMOKE_CONVERSATION`, `CHAT_SMOKE_FOREIGN_CONVERSATION`.
Для проверки typing payload передайте `CHAT_SMOKE_PEER_TOKEN` от BUSINESS_MEMBER того
же диалога. При частном CA задайте `NODE_EXTRA_CA_CERTS` с доверенным PEM.

```powershell
npm ci --prefix tools/consumer-smoke
node tools/consumer-smoke/signalr-smoke.mjs
```

Официальный JS-клиент через WSS проверяет действующий токен, отказ при неверном токене,
запрет Join/Leave/Typing для чужого диалога и допустимые вызовы. При peer токене проверяет
форму события. Browser и React Native проходят отдельный прогон в consumer PR.

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

Для Этапа 1 используйте [матрицу готовности B01–B05](hardening/B01-B05-release-readiness.md).
На восстановленной копии прогоните preflight миграции и сверьте канонические сообщения,
историю заявок и ссылки на объекты. Сроки хранения, включение удаления и legal hold
согласуются до публичного запуска. [Политика данных](data-model.md#retention-b054)
