# Публикация backend на Linux через Docker Compose

`chat.dodapp.kz` обслуживает существующий Nginx на Linux-хосте. Compose запускает
одну Production-реплику ASP.NET Core и PostgreSQL 17 с постоянным volume.
API слушает `127.0.0.1:15105` на хосте. PostgreSQL доступен только в закрытой сети проекта.
Nginx проверяет внутренний TLS-сертификат API. API доверяет forwarded-заголовкам от
точного адреса шлюза Docker для запросов с хоста и от loopback для собственного healthcheck.
Приложение использует владельца своей БД `chat` без SUPERUSER/CREATEDB/CREATEROLE.
Пароль администратора PostgreSQL отдельный.

Нужны Linux-сервер, Docker Engine 28+ с Compose v2 (`up --wait`), OpenSSL, `flock` и
публичный сертификат для домена в Nginx. Ограничение по версии Engine связано с
[изоляцией портов, привязанных к loopback](https://docs.docker.com/engine/network/port-publishing/).

Можно перенести подготовленный архив `artifacts/chat-backend-server.tar.gz` на сервер
и распаковать в отдельную папку, например `/opt/chat-backend`. Архив содержит исходники
и конфигурацию публикации. Выполняйте команды из этой папки.

```sh
sudo install -d /opt/chat-backend
sudo tar -xzf chat-backend-server.tar.gz -C /opt/chat-backend
cd /opt/chat-backend
```

## 1. Настройки

В корне checkout:

```sh
sudo install -m 600 .env.production.example .env.production
openssl rand -hex 32
openssl rand -hex 32
openssl rand -base64 32
openssl rand -base64 32
```

Заполните четыре независимых секрета: POSTGRES_PASSWORD, POSTGRES_ADMIN_PASSWORD,
JWT_SIGNING_KEY_BASE64, MAIL_QUEUE_KEY_BASE64. Пароли PostgreSQL в этом шаблоне должны
быть hex, чтобы не создавать разделители в Npgsql connection string. Укажите API_DOMAIN
без схемы/пути, точный WEB_ORIGIN и уникальный APP_TAG релиза (например Git SHA).
Для значений с `$` в dotenv используйте одинарные кавычки, чтобы Compose не подставлял переменные.
Не публикуйте вывод `docker compose config`: он содержит развёрнутые секреты; используйте `config --quiet`.

Рабочие email и вложения требуют существующих сервисов:

- SMTP с STARTTLS, учётными данными и разрешённым отправителем — SMTP_*.
- Private S3 bucket с HTTPS — S3_*. Endpoint должен быть доступен также браузеру и mobile,
  поскольку API выдаёт signed URLs. Настройте CORS хранилища для WEB_ORIGIN и нужные PUT/GET.
- ClamAV clamd в доверенной сети — CLAMAV_HOST/PORT. Должен быть доступен контейнеру API;
  `127.0.0.1` внутри контейнера означает сам контейнер. Включите AlertEncrypted/AlertExceedsMax,
  обновление сигнатур и сетевые ACL. Не открывайте clamd в интернет.

Эти три сервиса не подменяются Mailpit/фальшивым scanner в production Compose. Redis пока
не используется реализацией и в этот стек не добавлен. Retention остаётся выключенным
до явного RETENTION_APPROVED=true после согласования политики. PUSH_ENABLED требует
проверенной настройки Expo. Сохраняйте mail key при обновлении/восстановлении очереди.

Перед запуском проверьте, что `127.0.0.1:15105` свободен (`ss -ltn`) и что сеть
`10.253.84.0/24` не пересекается с сетями Docker, хоста и VPN. При конфликте задайте
свободный `API_PORT` и укажите тот же порт в `proxy_pass`. `DOCKER_GATEWAY` должен
принадлежать `DOCKER_SUBNET`. Compose использует отдельное имя проекта `chat-production`.

## 2. Сертификаты

Внутренний CA и сертификат backend создаются один раз:

```sh
sudo sh deploy/production/init-internal-tls.sh
```

Скрипт не перезаписывает существующие ключи. Приватный ключ backend доступен uid 1654
(`app` в .NET image). CA key хранится только на сервере; в контейнеры он не монтируется.
Сертификат backend действует год: настройте контроль срока и обновление через тот же CA,
с SAN `chat-backend.internal`, затем перезапустите API/Nginx. Храните CA key и секреты
отдельно от backup БД в защищённой резервной копии.

Добавьте отдельный HTTPS-блок для `chat.dodapp.kz` рядом с существующими блоками
`api.dodapp.kz` (порт 5001) и `files.dodapp.kz` (порт 5002). Готовый блок
`deploy/production/nginx.chat.dodapp.kz.conf` использует сертификат с именем `chat.dodapp.kz`.
Чтобы проверить общий сертификат из показанной конфигурации, выполните на сервере:

```sh
sudo openssl x509 -in /etc/letsencrypt/live/dodapp.kz/fullchain.pem \
  -noout -checkhost chat.dodapp.kz
```

Если вывод содержит `does match certificate`, укажите в новом блоке оба пути из
`/etc/letsencrypt/live/dodapp.kz/`. Имя каталога сертификата само по себе не определяет,
какие домены он покрывает.
[Проверка имени сертификата в OpenSSL](https://docs.openssl.org/3.0/man1/openssl-x509/#certificate-checking-options).

Если вывод содержит `does NOT match certificate`, выпустите отдельный сертификат для
`chat.dodapp.kz`. Для плагина Nginx запись A и запись AAAA, если она задана, должны вести
на сервер, а порт 80 должен быть доступен извне. Выполните на Linux-хосте до установки
нового HTTPS-блока, который ссылается на ещё не выпущенный сертификат:

```sh
sudo nginx -t
sudo certbot certonly --nginx --cert-name chat.dodapp.kz -d chat.dodapp.kz
sudo openssl x509 -in /etc/letsencrypt/live/chat.dodapp.kz/fullchain.pem \
  -noout -checkhost chat.dodapp.kz
```

Используйте пути из вывода Certbot. Готовый блок уже указывает на
`/etc/letsencrypt/live/chat.dodapp.kz/fullchain.pem` и `privkey.pem` в том же каталоге.
При недоступном порте 80 используйте проверку DNS-01 через плагин своего DNS-провайдера.
[Команды Certbot](https://eff-certbot.readthedocs.io/en/stable/man/certbot.html) и
[проверки Let's Encrypt](https://letsencrypt.org/docs/challenge-types/).

Установите публичную часть внутреннего CA, которую Nginx использует для проверки API:

```sh
sudo install -m 644 deploy/production/tls/backend/ca.crt /etc/nginx/chat-backend-ca.crt
```

После выпуска сертификата вставьте всё содержимое `deploy/production/nginx.chat.dodapp.kz.conf`
в конец `/etc/nginx/sites-enabled/default`, после закрывающей скобки последнего `server`.
В файле собраны `map` и формат журнала на уровне `http`, отдельный `server` на `443 ssl`
и `location /` с прокси на контейнер. Если вы предпочитаете отдельный файл, вместо вставки
установите его в каталог, подключаемый из `http`, например:

```sh
sudo install -m 644 deploy/production/nginx.chat.dodapp.kz.conf /etc/nginx/conf.d/chat.dodapp.kz.conf
```

Для `chat.dodapp.kz` оставьте один HTTPS-блок. Если ранее установлен вариант с `include`,
замените его новым. Docker публикует порт `8443` контейнера API как `127.0.0.1:15105`
на Linux-хосте. Для Nginx на хосте это upstream `https://127.0.0.1:15105`.
Имя `chat-backend.internal` в `proxy_ssl_name` используется для проверки сертификата API.
Заголовки `Upgrade` и `Connection` обеспечивают работу SignalR через WebSocket.
[WebSocket в Nginx](https://nginx.org/en/docs/http/websocket.html).

Проверьте конфигурацию и перечитайте её:

```sh
sudo nginx -t && sudo systemctl reload nginx
```

После запуска API проверьте `curl --fail https://chat.dodapp.kz/health/ready`.

## 3. Первый запуск и обновления

```sh
sudo sh deploy/production/release.sh
```

Скрипт проверяет Compose, собирает образ, запускает PostgreSQL, останавливает старый API,
создаёт и проверяет `backups/pre-release-UTC.dump`, применяет миграции контейнером `migrate`
и запускает API с ожиданием readiness. Nginx на хосте продолжает обслуживать другие приложения.
Используется stop-first: второй API на той же БД запрещён guard. Миграции не запускаются
в обычном старте API; флаг `--migrate` не запускает HTTP и фоновые workers.
При ошибке backup или миграции скрипт останавливается, API остаётся остановлен.
Планируйте окно обслуживания, особенно для построения индексов RetentionThroughput.

Compose profile `ops` содержит migrate; обычный `up` его не запускает. Для ручной работы:

```sh
docker compose --env-file .env.production -f deploy/docker-compose.production.yml config --quiet
docker compose --env-file .env.production -f deploy/docker-compose.production.yml ps
docker compose --env-file .env.production -f deploy/docker-compose.production.yml logs --tail 100 api postgres
curl --fail https://chat.dodapp.kz/health/ready
```

Если Nginx возвращает `502`, проверьте TLS и маршрут до API прямо на хосте:

```sh
curl --fail --cacert deploy/production/tls/backend/ca.crt \
  --resolve chat-backend.internal:15105:127.0.0.1 \
  -H 'Host: chat.dodapp.kz' -H 'X-Forwarded-For: 127.0.0.1' -H 'X-Forwarded-Proto: https' \
  https://chat-backend.internal:15105/health/ready
```

Healthcheck API проверяет readiness по настоящему TLS с доверенным внутренним CA.
Проверка через домен подтверждает, что Nginx достиг API и принял оба TLS-сертификата.
Readiness проверяет БД, миграции и конфигурацию. SMTP, S3 и ClamAV проверяются отдельно.

Создание администратора после миграций, без пароля в shell history:

```sh
read -r -p 'Admin email: ' AdminSeed__Email
read -r -s -p 'Admin password (15–128 characters): ' AdminSeed__Password
echo
export AdminSeed__Email AdminSeed__Password
docker compose --env-file .env.production -f deploy/docker-compose.production.yml run --rm --no-deps \
  -e AdminSeed__Email -e AdminSeed__Password migrate --seed-admin
unset AdminSeed__Email AdminSeed__Password
```

Этот блок выполняйте в Bash. Проверьте HTTPS login, WSS SignalR/reconnect, получение письма,
загрузку/сканирование файла и отказ доступа к чужому диалогу с реальных клиентов.
Nginx пишет только `$uri`, без query string; JWT SignalR не попадает в access log.

## 4. Данные, backup и восстановление

Volume `chat-production_postgres-data` переживает пересоздание контейнеров и `down`.
Не используйте `down -v` на сервере с данными. Проект Compose должен сохранять одно имя:
смена имени проекта создаёт другой volume и выглядит как пустая БД.
Init-скрипт роли выполняется только на пустом volume; изменение пароля в dotenv не меняет
пароль существующей роли. Для ротации используйте контролируемый ALTER ROLE и обновление
конфигурации/перезапуск. Не меняйте major PostgreSQL простым изменением image tag.

Backup перед release не заменяет регулярное резервирование. Отправляйте зашифрованные
dump off-host по утверждённому расписанию и проверяйте восстановление в отдельном окружении.
Для большего объёма и заданного RPO нужен WAL/PITR. Резервируйте также private bucket,
ключи и настройки; восстановление одной БД не восстанавливает вложения.

На **отдельном пустом стенде**, после создания ролей и до запуска API:

```sh
docker compose --env-file .env.production -f deploy/docker-compose.production.yml exec -T postgres \
  pg_restore -U postgres -d chat --no-owner --role=chat < backups/SELECTED.dump
```

После восстановления проверьте миграции, readiness, сообщения и ссылки на S3. При неудачном
обновлении выбирайте roll-forward либо согласованное восстановление backup; не запускайте
автоматический Down миграций поверх новых данных. Прежний APP_TAG можно использовать только
после проверки совместимости схемы.

Образы закреплены по версиям .NET и major PostgreSQL. Перед публичным релизом закрепите
проверенные digests, выполните scan образов и обновите патчи по принятому процессу.
Single-host Compose не обеспечивает HA. Масштабирование API >1 пока не поддерживается.

Локальная проверка прошла на Docker Engine 29.2.1 с тестовыми сертификатами и секретами.
Подтверждены сборка образа, запуск PostgreSQL с отдельной ролью `chat`, восемь миграций,
повторный запуск миграций, чтение оглавления `pg_dump` через `pg_restore --list`, HTTPS readiness
через опубликованный loopback-порт и проксирование с проверкой сертификата API в тестовом
Nginx. Прошёл 51 тест backend, включая OpenAPI и интеграцию с PostgreSQL. На целевом сервере
осталось проверить публичный сертификат, `/health/ready` через действующий Nginx, SMTP, S3 и ClamAV.

Схема запуска согласована с [Docker Compose startup order](https://docs.docker.com/compose/how-tos/startup-order/)
и [официальным PostgreSQL image](https://hub.docker.com/_/postgres).
