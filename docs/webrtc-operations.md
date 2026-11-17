# B06: инфраструктура звонков

Backend передаёт только signaling. Медиа идёт между устройствами либо через отдельный TURN; API не принимает RTP и не записывает звонки. Существующее ограничение на один API-процесс с локальным реестром SignalR-подключений сохраняется.

## Конфигурация

- Установить `Calls__Enabled=true` только после подготовки TURN. По умолчанию звонки отключены, создание и выдача ICE отвечают 503.
- `Calls__TurnSecret`: отдельный случайный секрет минимум 32 UTF-8 байта из secret store. Тот же секрет настроить как coturn `static-auth-secret`, включить `use-auth-secret`; не использовать JWT signing key.
- `Calls__TurnUrls__0=turn:turn.example.org:3478?transport=udp`; при необходимости добавить индексы 1–3 для TCP и `turns:turn.example.org:5349?transport=tcp`.
- `Calls__StunUrls__0=stun:turn.example.org:3478` необязателен. STUN сам по себе не обеспечивает работу за restrictive NAT.
- `Calls__CredentialSeconds=300`, разрешено 60–600. Реальный срок ограничен TTL Call. Часы API и coturn должны быть синхронизированы.
- `Media__ProbePath=ffprobe`, либо абсолютный путь к установленному executable. Runtime Dockerfile устанавливает пакет ffmpeg. На Windows executable устанавливается отдельно.

При `Enabled=true` неполная TURN-конфигурация делает `/health/ready` неготовым. Эта проверка не выполняет TURN allocation и не подтверждает доступность relay или ffprobe. Проверить executable и реальный TURN отдельно перед включением.

## TURN и сеть

Развернуть coturn отдельно от API с публичным DNS/IP и корректным external-ip при NAT. Открыть listener UDP/TCP 3478, TLS TCP 5349 и согласованный диапазон UDP relay-портов; ограничить диапазон в coturn и firewall одинаково. Настроить настоящий сертификат для `turns`, realm, bandwidth/allocation/user quotas, запрет loopback/link-local/private peer destinations, административный доступ и защиту от abuse. Сетевые ограничения должны учитывать фактическую топологию, включая IPv6. Не открывать управление coturn в интернет.

Клиент получает короткоживущие credentials только через авторизованный `POST /v1/calls/{id}/ice-config`, обновляет их до `expiresAt` и использует в RTCPeerConnection. Credentials нельзя писать в логи. Shared secret не передаётся клиентам. HMAC соответствует [coturn REST authentication](https://github.com/coturn/coturn/blob/master/README.turnserver).

Выданный credential может использоваться до истечения срока независимо от дальнейшего Call state. API не может немедленно остановить P2P-поток и не отзывает уже открытую TURN allocation. Клиенты обязаны закрыть tracks/peer connection после завершения, потери доступа и terminal REST reconciliation. Реальное время жизни allocation нужно ограничить и проверить в coturn; TTL credentials не заменяет эту настройку.

## HTTPS/WSS и медиа

Сохранить production transport из B05: HTTPS до Kestrel, доверенный IP непосредственного прокси, отсутствие публичного порта Kestrel, WebSocket upgrade. При разрешённом выпуске 16–17 ноября дополнительно проверены публичная цепочка сертификатов, loopback binding Kestrel, проверка upstream TLS в Nginx и авторизованный WSS через действующий production reverse proxy.

ffprobe запускается без shell с ограничением времени, параллельности, размера вывода и разрешённых протоколов/форматов. Это инспекция metadata, а не полная декодировка и не доказательство воспроизводимости всего файла. Обновлять ffmpeg security patches, ограничить память/CPU процесса средствами ОС/контейнера, tmpfs и egress. Производственный tmpfs увеличен до 192 МиБ для двух временных файлов до 50 МиБ; общие лимиты upload, memory и concurrent requests также нужно согласовать с нагрузкой. После инспекции остаётся обязательный ClamAV и READY gate.

Для объектов до 50 МиБ согласовать clamd `StreamMaxLength`, `MaxFileSize`, `MaxScanSize` и сетевые таймауты с этим пределом. Значения по умолчанию могут быть меньше. Отказ сканера оставляет объект PENDING; это не разрешение скачать или отправить непроверенный файл.

## Проверка перед интеграцией

1. Применить и проверить миграцию CallsAndTimedMedia на копии рабочей БД; rollback удаляет Call и media metadata.
2. Согласовать OpenAPI 0.6.0 и events-v1 с F15/M09, включая positional arguments и revision.
3. На browser + физическом mobile проверить permissions, codecs, реальный audio/video, переключение сети и reconnect.
4. Принудительно установить `iceTransportPolicy: 'relay'`, подтвердить selected candidate pair `relay` и двусторонние медиа через UDP и TCP/TLS fallback.
5. Проверить NAT, истечение/обновление credentials, busy/reject/timeout, logout/suspension, потерю сети и закрытие tracks при terminal state.
6. Проверить производственную TLS/WSS-цепочку и нагрузку Outbox/ACL/CallWorker. Задержки worker могут превышать интервал опроса при backlog.

На целевом TURN выполнены внешние allocation/relay UDP/TCP/TLS, но проверки физических устройств ещё не выполнены. Физические звонки не имеют статуса PASS. См. [фактический deployment report](deployments/B06-2026-11-17.md).

## Production overlay

`deploy/docker-compose.webrtc.yml` применяется вместе с существующим base Compose. Сначала provision server-only `deploy/production/private/turn/turnserver.conf` по template, отдельный общий secret в `.env.production`, TLS-файлы в `deploy/production/tls/turn` и firewall. Не заменять site-specific base Compose шаблоном: он содержит настройки существующих S3/email/ClamAV. Все placeholders template должны быть заменены на сервере; не сохранять полученную конфигурацию в Git.

coturn 4.19 запускается с `-c /etc/coturn/turnserver.conf`; CLI и DTLS по умолчанию отключены, минимальный TLS 1.2. Непривилегированному executable необходима capability NET_BIND_SERVICE в bounding set; остальные capabilities отключены. Не добавлять `denied-peer-ip=::`: в этой версии это wildcard, блокирующий relay. Текущий template запрещает private/link-local/loopback peers; при изменении топологии отдельно пересмотреть IPv6.

`deploy/production/renew-turn-tls.sh` — scoped hook для этого домена и `/home/ubuntu/chat`: копирует обновлённый сертификат/ключ с владельцем 65534 и mode 400 и перезапускает только coturn. Проверить фактическое renewal и актуальный fingerprint при следующем продлении; установка hook сама по себе этого не доказывает.

`release.sh` подключает overlay, только если существуют overlay и provisioned private turnserver.conf. Первый запуск coturn выполняется отдельно через `docker compose ... -f deploy/docker-compose.webrtc.yml up -d --wait coturn`; release API не подменяет provisioning. Миграция запускается без stdin (`-T migrate </dev/null`), stop-first и backup обязательны.
