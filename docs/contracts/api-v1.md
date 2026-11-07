# Контракт REST API — chat (v0.1.0)

**Статус:** согласованный проектный baseline для трёх репозиториев; до реализации это ТЗ, а не опубликованный работающий API.  
**Источник истины после реализации:** `backend` (ASP.NET Core Web API) — OpenAPI, экспортируемый из кода и проверяемый CI.  
**Синхронизация:** одинаковая копия этого файла в `backend`, `frontend`, `mobile`. При изменениях повышать версию и обновлять все потребители в отдельных PR.

## Базовые правила

- Base URL: `https://<api-host>/v1` (домены окружений через `.env`, не хардкодить).
- JSON: `camelCase`, даты в ISO 8601 UTC, id — UUID, HTTP over TLS.
- `Authorization: Bearer <accessToken>`; refresh-токен имеет ротацию и отзывание. Не пересылать токены в URL.
- Ошибки: `{ "error": { "code": "FORBIDDEN", "message": "...", "requestId": "...", "details": null } }`. Коды: `VALIDATION_ERROR`, `UNAUTHENTICATED`, `FORBIDDEN`, `NOT_FOUND`, `CONFLICT`, `RATE_LIMITED`, `VERIFICATION_REQUIRED`, `SUSPENDED`, `MEDIA_NOT_READY`, `INTERNAL_ERROR`.
- Пагинация: `?cursor=<opaque>&limit=20`, ответ `{ "items": [...], "nextCursor": null | "..." }`; стабильная сортировка по времени + id.
- `userKind`: `CLIENT` | `BUSINESS_MEMBER` | `PLATFORM_ADMIN`. `businessRole`: `OWNER` | `ADMIN` | `AGENT`. Роли не определяют тип диалога: участники диалога всегда `clientUserId` и `businessId`.
- Клиент общается **только** с верифицированной активной компанией; ни `CLIENT↔CLIENT`, ни `BUSINESS↔BUSINESS` эндпоинтами не поддерживаются. Сотрудник отвечает от имени компании; личный id оператора не раскрывается клиенту.
- MVP: клиент первым создаёт диалог. У компании нет API для массовых/непрошеных исходящих сообщений. Это зафиксированное рабочее допущение до отдельного продуктового решения.
- MVP: доступ бесплатный для всех. Биллинг, планы, платежи и лимиты тарифов **не реализовывать**; сохранить в документации как future.

## Авторизация и аккаунты — Этап 1

| Метод | Маршрут | Кто | Назначение |
|---|---|---|---|
| `POST` | `/auth/client/register` | Гость | Регистрация клиента: email + пароль + displayName |
| `POST` | `/auth/login` | Гость | Login: email + пароль; ответ с токенами и `userKind` |
| `POST` | `/auth/refresh` | Сессия | Ротация refresh-токена |
| `POST` | `/auth/logout` | Авторизован | Отозвать refresh-сессию |
| `POST` | `/auth/email/verify` | Гость с токеном | Подтверждение email |
| `POST` | `/auth/password/forgot` | Гость | Запрос сброса пароля без раскрытия существования учётки |
| `POST` | `/auth/password/reset` | Гость с токеном | Смена пароля |
| `GET` | `/me` | Авторизован | `{id, userKind, displayName, email, emailVerified, businessMemberships[]}` |

Выбранный для MVP способ регистрации клиента — **email/пароль**. Телефонный OTP пока не утверждён и не входит в этот baseline. Mobile не содержит бизнес-регистрации; компания и её владелец создаются **исключительно в веб-сценарии** подачи заявки. Backend проверяет бизнес-правила, документы и статус; полагаться на `User-Agent`/CORS для защиты регистрации нельзя.

## Регистрация компании, проверка, каталог — Этап 1

| Метод | Маршрут | Кто | Назначение |
|---|---|---|---|
| `POST` | `/business-applications` | Гость | Веб-регистрация компании и owner, заявка `PENDING_VERIFICATION` |
| `GET` | `/my-business` | OWNER / ADMIN | Профиль и состояние заявки своей компании |
| `PATCH` | `/my-business` | OWNER / ADMIN | Разрешённые поля бизнес-профиля |
| `GET` | `/businesses` | CLIENT | **Только** `VERIFIED` + `ACTIVE`, поисковая строка `q`, `cursor`, `limit` |
| `GET` | `/businesses/:businessId` | CLIENT | Только публичный профиль верифицированной активной компании |
| `GET` | `/admin/business-applications` | PLATFORM_ADMIN | Очередь проверки, фильтр по статусу |
| `GET` | `/admin/business-applications/:id` | PLATFORM_ADMIN | Детали заявки и приватных файлов |
| `POST` | `/admin/business-applications/:id/decision` | PLATFORM_ADMIN | `{decision:"APPROVE"|"REJECT",reason?}`; audit trail |

`POST /business-applications`: `{ owner: {email,password,displayName}, business: {legalName,displayName,registrationNumber,country,category,description?}, verificationDocumentIds: [] }`. Файлы подтверждения передаются через отдельный защищённый процесс загрузки; хранить приватно. В MVP можно предложить адаптер временного загрузчика для гостевой заявки с краткоживущим `applicationDraftToken`; нельзя выдавать произвольные публичные ссылки на документы.

Состояния бизнеса: `PENDING_VERIFICATION`, `VERIFIED`, `REJECTED`, `SUSPENDED`. В публичном поиске отображается только `VERIFIED` и активный профиль. Владелец может видеть статус в вебе до проверки, но не использовать мобильный бизнес-режим до `VERIFIED`.

## Диалоги и сообщения — Этап 1

| Метод | Маршрут | Кто | Назначение |
|---|---|---|---|
| `POST` | `/conversations` | CLIENT | `{businessId}`; вернуть существующий или создать единственный диалог на пару |
| `GET` | `/conversations` | CLIENT / BUSINESS_MEMBER | Список только доступных диалогов, `cursor`, `limit` |
| `GET` | `/conversations/:id` | Участник | Метаданные, права и статус |
| `GET` | `/conversations/:id/messages` | Участник | История по cursor, упорядочена стабильно |
| `POST` | `/conversations/:id/messages` | Участник | `{clientMessageId,type,text?,attachmentIds?}`; идемпотентность |
| `POST` | `/conversations/:id/read` | Участник | `{lastMessageId}`; обновить состояние прочитанного |
| `POST` | `/uploads/presign` | Участник | Начать безопасную загрузку изображения/документа (phase 1) |
| `POST` | `/uploads/:uploadId/complete` | Владелец upload | Завершить с проверкой объекта и метаданных |

В MVP `type=TEXT|IMAGE|FILE`. `AUDIO|VIDEO` как типы вложений добавляются в Этапе 2. Отправка ссылки на несуществующий/чужой/неготовый attachment запрещена. `clientMessageId` — UUID, уникальный в пределах автора/диалога; повторный запрос возвращает ранее созданное сообщение без дубля. При отсутствии доступа использовать одинаково аккуратный `404` или `403`, не раскрывающий чужие диалоги.

Пример `Message`: `{id,conversationId,clientMessageId,authorSide:"CLIENT"|"BUSINESS",type:"TEXT",text:"Здравствуйте",attachments:[],createdAt,editedAt:null}`. Отдельный внутренний `authorUserId` хранится в БД, но не выдаётся клиенту для сообщений от бизнеса.

## Этап 2: голос, видео, звонки

| Метод | Маршрут | Кто | Назначение |
|---|---|---|---|
| `POST` | `/calls` | Участник | `{conversationId,kind:"AUDIO"|"VIDEO"}`; инициировать 1:1 звонок |
| `GET` | `/calls/:id` | Участник | Статус звонка и разрешённый участник |
| `POST` | `/calls/:id/end` | Участник | Завершение, идемпотентно |
| `POST` | `/calls/:id/ice-config` | Участник | Краткоживущая ICE/TURN-конфигурация |

WebRTC SDP/ICE идут через аутентифицированные realtime события (см. events-v1). Звонки строго между одним клиентом и одним оператором компании, без записи, групповых созвонов или общего доступа к медиа потоку на backend.

## Этап 3: инструменты бизнеса

| Метод | Маршрут | Кто | Назначение |
|---|---|---|---|
| `POST` | `/my-business/invitations` | OWNER / ADMIN | Приглашение агента |
| `POST` | `/invitations/accept` | Приглашённый | Присоединение к проверенной компании |
| `GET` | `/my-business/members` | OWNER / ADMIN | Сотрудники и роли |
| `PATCH` | `/my-business/members/:id` | OWNER / ADMIN | Безопасная смена роли/отключение |
| `PATCH` | `/conversations/:id/assignment` | OWNER / ADMIN | Назначение ответственного |
| `GET` | `/my-business/analytics` | OWNER / ADMIN | Базовые метрики обработки обращений |

API-first: изменения описываются сначала в backend OpenAPI, затем обновляются снимки контракта и типы в consumer-репозиториях. Нельзя молча менять поля, маршруты, роли, event names.
