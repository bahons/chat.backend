# Backend: безопасность, верификация, приватность

## Границы доверия

- Public: регистрация клиента, вход, **веб-заявка бизнеса**; protection: rate limit, captcha/abuse defense where needed, проверка email, валидация юридических атрибутов и документов.
- Authenticated CLIENT: каталог VERIFIED, собственные диалоги; запуск нового диалога только со VERIFIED+ACTIVE.
- Authenticated BUSINESS_MEMBER: только свой бизнес, собственные доступные диалоги, запрет мобильного доступа до бизнес-верификации. OWNER/ADMIN/AGENT — минимально необходимые полномочия.
- PLATFORM_ADMIN: создаётся только защищённой административной процедурой (seed/CLI/manual invite) и может проводить review; не публичная self-signup роль. Все решения журналировать.
- Нельзя доверять скрытым UI-кнопкам: middleware/guards/DB checks обязательны для **каждого** endpoint и SignalR Hub methods.

## Веб-только регистрация бизнеса

Продуктовое требование означает: mobile UI не имеет регистрации бизнеса, а официальный onboarding-сценарий доступен только через web. Но серверный endpoint публичной веб-заявки технически может вызвать любой HTTP-клиент. Не пытаться выдавать `User-Agent`, `Origin`, referer или CORS за надёжное доказательство, что заявка создана веб-браузером. Реальная защита — верификация, anti-abuse и выдача прав лишь после проверки.

## Аутентификация

- Пароли — Argon2id или сопоставимый современный KDF; strong password policy; сброс одноразовым short-lived токеном.
- JWT access short-lived; refresh-токен rotation + hash-at-rest + replay detection + revoke chain. Для SignalR transport проверить поведение `access_token`: если библиотека помещает токен в query во время WebSocket handshake, принимать его только на Hub path, требовать WSS и исключить query-string из логов.
- Web: refresh предпочтительно в `Secure; HttpOnly; SameSite` cookie с CSRF-защитой при cookie-auth; access token по выбранной безопасной схеме, не хранить долгоживущие секреты в localStorage.
- Mobile: токены в системном secure storage; не AsyncStorage. Все API через TLS/WSS.
- В вебе и мобильных клиентах корректно обрабатывать expired/rotated token, session revocation, role change.

## Верификация бизнеса

- Документы private, upload signed URL краткоживущие; malware scanning или явно документированный безопасный карантин в MVP.
- Результат review с `actor, when, decision, reason`; business profile не показывать в каталоге до APPROVE.
- Проверка регистрационных документов может зависеть от страны. Не заявлять автоматическую юридическую верификацию без интеграции и правил юрисдикции.
- Suspended company скрывается из поиска; отправка сообщений блокируется по серверной политике; сообщить существующим клиентам нейтральную причину недоступности.

## Медиабезопасность

- Проверять MIME по содержимому, размер, extension, ownership, allowed types. Изолировать storage ключи и private URLs, ограничивать TTL и привязывать выдачу к ACL диалога.
- Нельзя публиковать verification docs в том же бакете/префиксе, что пользовательские вложения без разделения политик.
- Ограничить URL previews и загрузку внешних ссылок, чтобы не открыть SSRF.

## Не забыть к production

Threat model, OWASP ASVS/MASVS relevant guidance, dependency/SAST/secret scans, audit, abuse reports/blocking, rate limits per actor and IP, backups + restore test, secret rotation, data residency/legal review, retention/deletion/export policies, monitoring without PII.
