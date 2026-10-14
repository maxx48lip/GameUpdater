# Серверная часть — VPS (Debian 12, Docker, Nginx, SFTP)

Подготовка **VPS на Debian 12 x64** для автообновления игрового клиента:

- **Nginx в Docker** (`fileserver`) отдаёт статику по HTTPS: `version.json`, `manifest.json`, `files/*`;
- **OpenSSH SFTP** (порт 22) принимает заливку от Exporter (см. корневой README, `../README.md`);
- **Launcher** (см. `GameUpdater/src/GameUpdater.Launcher/README.md`) скачивает файлы по HTTPS.

Ограничения сервера: **750 МБ RAM, 1 ядро CPU, SSD**. Запрещены БД и панели управления —
только Nginx в Docker + SFTP (OpenSSH).

## Состав

| Файл | Назначение |
|---|---|
| `setup-vps.sh` | Скрипт первичной настройки Debian 12: система, пользователи, SSH, UFW, Docker, Nginx, certbot. **Идемпотентен**, самодостаточен (все конфиги встроены) |
| `nginx.conf` | Эталонная копия конфига, который скрипт ставит в `/etc/nginx/conf.d/fileserver.conf` (для домена `files.example.com`) |
| `sshd_config_snippet` | Эталонная копия фрагмента sshd, который скрипт ставит в `/etc/ssh/sshd_config.d/10-gamefiles.conf` |
| `local-test.sh` | Локальный сценарий тестирования: Nginx в Docker на localhost + тестовая сборка |

## Архитектура

```text
                    SFTP :22 (ключи, chroot)                 HTTPS :443 / HTTP :80
  Exporter ──────────────────────────────► sshd ─┐            ▲
  wiki/ → files → manifest.json → version.json   │           │
                                                  ▼           │
                                    /srv/gamefiles/          │
                                    ├── www/ ◄── запись      │
                                    │    ├── version.json    │
                                    │    ├── manifest.json   │
                                    │    ├── files/...       │
                                    │    └── wiki/...        │
                                    └── (chroot-корень, ro)  │
                                                  │           │
                                          bind mount :ro      │
                                                  ▼           │
                                   docker: fileserver        │
                                   nginx:alpine ◄─────────────┘
                                                  ▲
                                                  │ GET /version.json, /manifest.json, /files/*
                                            Launcher
```

## Структура каталогов на сервере (важно!)

```text
/srv/                          root:root        755
/srv/gamefiles/                root:root        755   ← ChrootDirectory (только чтение для exporter)
/srv/gamefiles/www/            exporter:exporter 755   ← единственная записываемая область = web-корень Nginx
    version.json               exporter         644
    manifest.json              exporter         644
    files/                     exporter         755
        client.exe             exporter         644
        data/level1.pak        exporter         644
    wiki_manifest.json         exporter         644     ← path + size + MD5 файлов wiki (дельта-заливка)
    wiki/                      exporter         755     ← статическая wiki (если задан "wikiFolder" в Exporter)
        index.html             exporter         644     → https://<домен>/wiki/
        pages/*.html           exporter         644
```

> **Wiki:** содержимое папки из `"wikiFolder"` конфига Exporter заливается в `www/wiki/`
> (sftp-путь `/www/wiki/...`) и отдаётся по адресу `https://<домен>/wiki/`. Wiki **не входит**
> ни в `manifest.json`, ни в `version.json` — Launcher её не видит; это просто статика для браузера.
> Ссылки внутри wiki относительные, поэтому перекрёстные переходы работают без изменений.
> Обновляется только с флагом `--update-wiki` и **дельтой**: Exporter сравнивает MD5 из локального
> сканирования с удалённым `wiki_manifest.json`, грузит лишь изменившиеся файлы, удаляет устаревшие
> и публикует новый манифест последним (см. корневой README).

> **Почему `www/`, а не файлы прямо в `/srv/gamefiles`?**
> OpenSSH требует, чтобы `ChrootDirectory` и все его родители принадлежали **root**
> и не были записываемыми — иначе sshd откажется chroot'ить пользователя. Поэтому
> `/srv/gamefiles` — chroot-корень, а запись идёт в подпапку `www/` внутри него.
> Отсюда два практических следствия:
>
> 1. Nginx монтирует **`/srv/gamefiles/www`** (а не `/srv/gamefiles`) как read-only volume;
>    URL-схема при этом не меняется: `https://files.example.com/version.json`, `/manifest.json`, `/files/<путь>`.
> 2. В конфиге Exporter для этого сервера **`remoteBasePath` = `"/www"`**: sftp-пути —
>    абсолютные *внутри chroot* (`/www/files/client.exe` → на диске `/srv/gamefiles/www/files/client.exe`).

Права файлов 644/755 достаточны для Nginx в контейнере (работает от непривилегированного
пользователя, ему хватает `o+r`).

## Развёртывание на VPS — пошагово

### Шаг 0. Требования

- Чистый **Debian 12 x64** VPS, доступ root (SSH по ключу или веб-консоль провайдера).
- Домен (например `files.example.com`) с **A-записью на IP VPS**. Запись должна быть
  в силе *до* запуска скрипта — иначе Let's Encrypt не выдаст сертификат.

### Шаг 1. DNS

Создайте A-запись у регистратора/DNS-провайдера:

```text
files.example.com.  A  <IP вашего VPS>
```

Проверьте резолвинг (должен вернуть IP VPS):

```bash
dig +short files.example.com
# или: getent hosts files.example.com
```

### Шаг 2. Запуск setup-vps.sh

Скопируйте скрипт на VPS (`scp server/setup-vps.sh root@<ip>:~/`) и выполните:

```bash
DOMAIN=files.example.com EMAIL=admin@example.com sudo ./setup-vps.sh
```

Без SSL-сертификата (напр. за **Cloudflare Tunnel**, где TLS уже терминируется на стороне
туннеля и у поддомена есть свой сертификат) — пропускаются certbot и Let's Encrypt, nginx
отдаёт plain HTTP на `:80`:

```bash
NO_SSL=1 DOMAIN=files.example.com sudo ./setup-vps.sh
```

Ключи **не нужно передавать**: если `ROOT_PUBKEY`/`EXPORTER_PUBKEY` не заданы, скрипт сам
создаёт пары ed25519 (если их ещё нет), ставит публичные части на сервер и выводит
приватные в консоль — сохраните их (см. шаг 3).

| Переменная | Обязательна | Назначение |
|---|---|---|
| `DOMAIN` | да | Домен/поддомен для Nginx (и Let's Encrypt, если не `NO_SSL=1`) |
| `EMAIL` | да, кроме `NO_SSL=1` | Email аккаунта Let's Encrypt |
| `NO_SSL` | нет (`0`/`1`) | `1` — развёртывание без SSL: без certbot/Let's Encrypt, nginx отдаёт plain HTTP на `:80`, UFW не открывает 443. Для Cloudflare Tunnel и т.п., где TLS снаружи |
| `ROOT_PUBKEY` | нет | Свой публичный ключ админа → `/root/.ssh/authorized_keys` (вместо автогенерации) |
| `EXPORTER_PUBKEY` | нет | Свой публичный ключ SFTP-заливки → `/home/exporter/.ssh/authorized_keys` (вместо автогенерации) |

Ключ передаётся строкой (`"ssh-ed25519 AAAA..."`) или путём к `.pub` файлу.

> **CI/пайплайны:** если вывод скрипта логируется, не давайте ему генерировать ключи —
> передайте свои через `ROOT_PUBKEY`/`EXPORTER_PUBKEY`, иначе сгенерированные приватные
> ключи окажутся в логах.

Что делает скрипт:

1. `apt update && apt upgrade`; ставит `openssh-server`, `ufw`, `docker-ce` и `certbot`
   (`certbot` — только если не `NO_SSL=1`). При отсутствии swap создаёт 1 ГБ `/swapfile`
   (страховка от OOM на 750 МБ RAM).
2. Создаёт пользователя `exporter` (`nologin`) и каталоги с правами из таблицы выше.
3. Ключи: если не переданы свои, создаёт пары ed25519 — админскую `/root/.ssh/id_ed25519`
   и SFTP-ключ `/root/exporter-sftp-key`; публичные части ставит в `authorized_keys`,
   приватные выводит в консоль (только при создании). Существующие ключи не перегенерируются.
4. Ставит sshd-конфиг: **только ключи**, root — без пароля, chroot + `internal-sftp` для exporter;
   проверяет `sshd -t`, делает `systemctl reload ssh`.
5. UFW: разрешает 22/80 (и 443, если не `NO_SSL=1`), включает firewall (правила добавляются *до* включения).
6. Ставит nginx-конфиг в `/etc/nginx/conf.d/fileserver.conf` и поднимает контейнер
   `fileserver` (`nginx:alpine`, `--restart unless-stopped`). При `NO_SSL=1` — plain HTTP на :80,
   без 443 и без монтирования каталогов Let's Encrypt.
7. Let's Encrypt **webroot-режимом через сам контейнер** (ACME-челленджи отдаёт Nginx на :80):
   при первом запуске — временный HTTP-конфиг → `certbot certonly --webroot` → финальный TLS-конфиг.
   Ставит renewal-hook: после автопродления сертификата nginx в контейнере перезагружается.
   При `NO_SSL=1` весь этот шаг пропускается (сертификат и его продление не нужны).

Скрипт **идемпотентен**: повторный запуск безопасен (контейнер пересоздаётся, конфиги
перезаписываются тем же содержимым, ключи не дублируются и не перегенерируются). Если
сертификат не удалось выдать (DNS ещё не в силе) — поправьте DNS и запустите скрипт повторно.

### Шаг 3. Ключи: что сохранить и куда положить

Скрипт сам создал (или нашёл уже существующие) две пары ключей ed25519:

| Назначение | Приватный ключ на VPS | Куда положить приватную часть |
|---|---|---|
| Админский доступ (`ssh root@files.example.com`) | `/root/.ssh/id_ed25519` | На вашу машину, например `~/.ssh/id_ed25519_vps` |
| SFTP-заливка от Exporter | `/root/exporter-sftp-key` | На машину/CI, куда укажет `sftpKeyPath` в `exporter.json` |

Приватные ключи выводятся в консоль **только при их создании** (в блоках
`----- BEGIN PRIVATE KEY ... -----`). Если вы уже сохранили их или ключи существовали
ранее — возьмите содержимое с сервера:

```bash
sudo cat /root/.ssh/id_ed25519        # админский
sudo cat /root/exporter-sftp-key      # SFTP-ключ для Exporter
```

Сохранённый SFTP-ключ положите на машину/CI и укажите его в `exporter.json`
(`"sftpKeyPath": "~/.ssh/id_ed25519"` или любой другой путь; ключ без фразы пароля).

> Если вы хотите использовать **свои** ключи вместо сгенерированных — передайте их при
> запуске: `ROOT_PUBKEY="$(cat key.pub)" EXPORTER_PUBKEY="$(cat sftp-key.pub)"`.
> Для CI это обязательно (см. предупреждение в шаге 2).

Проверка SFTP с машины Exporter:

```bash
sftp -i ~/exporter-sftp-key exporter@files.example.com
sftp> ls            # виден только www/ — это chroot-корень /srv/gamefiles
sftp> cd ..         # не выйдет выше: «/» здесь и есть /srv/gamefiles
```

### Шаг 4. Первый деплой через Exporter

Конфиг `exporter.json` **для этого сервера** (обратите внимание на `remoteBasePath`):

```json
{
  "sftpHost": "files.example.com",
  "sftpPort": 22,
  "sftpUser": "exporter",
  "sftpKeyPath": "~/.ssh/id_ed25519",
  "remoteBasePath": "/www",
  "buildFolder": "/path/to/build",
  "ignorePatterns": ["*.log", "*.pdb", "local_version.json", ".DS_Store"],
  "wikiFolder": "/path/to/Wiki",
  "wikiIgnorePatterns": ["md", "scripts/__pycache__"],
  "minLauncherVersion": "1.0.0",
  "uploadMode": "perFile",
  "maxRetries": 3
}
```

> В примере из корневого README (`../README.md`) и в поставляемом `exporter.json` стоит
> `remoteBasePath: "/srv/gamefiles"` — это путь *на диске*. Для chroot-сервера Exporter
> должен использовать путь **внутри chroot**: `/www`.

Заливка (из папки решения GameUpdater):

```bash
dotnet run --project src/GameUpdater.Exporter \
  -- --version 1.0.5 --path ./build --config exporter.json

# + обновление wiki (дельта: только изменившиеся файлы, по MD5 из wiki_manifest.json)
dotnet run --project src/GameUpdater.Exporter \
  -- --version 1.0.5 --path ./build --config exporter.json --update-wiki
```

Если в конфиге задан `"buildFolder"`, аргумент `--path` можно опустить (CLI имеет приоритет).
Без `--update-wiki` wiki на сервере не трогается вовсе.

Порядок заливки соблюдает сам Exporter: сначала (если передан `--update-wiki`) статика wiki в
`/www/wiki/` + новый `/www/wiki_manifest.json`, затем все файлы в `/www/files/`, затем
`manifest.json`, последним — `version.json` (Launcher увидит новую версию только когда всё на
месте). Wiki не попадает в игровой манифест и видна по адресу `https://<домен>/wiki/`.

### Шаг 5. Проверка (чек-лист сервера)

```bash
# 1. version.json отдаётся с Cache-Control: no-cache
curl -sI https://files.example.com/version.json | grep -i cache-control
#    → Cache-Control: no-cache

# 2. manifest.json — то же самое
curl -sI https://files.example.com/manifest.json | grep -i cache-control
#    → Cache-Control: no-cache

# 3. Игровые файлы: immutable-кэш на год, gzip к .pak/.zip/.gz не применяется
curl -sI https://files.example.com/files/client.exe | grep -iE 'cache-control|content-encoding'
#    → Cache-Control: public, max-age=31536000, immutable   (без Content-Encoding)

curl -sI -H 'Accept-Encoding: gzip' https://files.example.com/files/data/level1.pak | grep -i content-encoding
#    → пусто (gzip к .pak не применяется; то же для .zip/.gz)

# 3b. Wiki (если задан "wikiFolder"): index отдаётся, страницы — no-cache (обновляются при заливке)
curl -sI https://files.example.com/wiki/ | grep -iE '^HTTP|cache-control'
#    → HTTP/2 200, Cache-Control: no-cache
curl -s https://files.example.com/wiki/pages/<страница>.html   # перекрёстные ссылки работают

# 4. SFTP: логин по ключу работает, shell недоступен
sftp -i ~/exporter-key exporter@files.example.com        # заходит, видит www/
ssh  -i ~/exporter-key exporter@files.example.com        # shell НЕ выдаётся (ForceCommand internal-sftp)

# 5. Chroot: выхода наружу нет
sftp> ls /srv       # → No such file or directory
sftp> cd / && pwd   # → /  (это и есть /srv/gamefiles на диске)

# 6. UFW: открыты только 22, 80, 443
ufw status verbose
#    → 22/tcp ALLOW, 80/tcp ALLOW, 443/tcp ALLOW; остальные — deny (incoming)

# 7. Контейнер переживает ребут
reboot
# после загрузки:
docker ps --filter name=fileserver   # → Up, restart policy unless-stopped
```

## Как это работает

### Кэширование и сжатие (nginx.conf)

| Пункт | Значение | Зачем |
|---|---|---|
| `sendfile on`, `tcp_nopush on` | — | Эффективная отдача статики без лишних копий |
| `open_file_cache max=2048 inactive=30s; open_file_cache_valid 5s` | — | Кэш открытых файлов. **Короткое valid-окно намеренно**: после деплоя файл из `files/*` может отдаваться по старому fd до 5 с; Launcher сверяет MD5 и ретраит скачивание (до 3 раз), поэтому окно перекрывается |
| gzip | Только текстовые типы (`application/json`, `text/plain`, …) | `.zip`, `.pak`, `.gz` уже сжаты — их MIME-типы в `gzip_types` не входят, gzip к ним **не применяется** |
| `/version.json`, `/manifest.json` | `Cache-Control: no-cache` | Метаданные всегда сверяются с сервером (Launcher запрашивает версию при каждом запуске) |
| `/files/*` | `Cache-Control: public, max-age=31536000, immutable` | Файлы не перекачиваются повторно; целостность гарантирует MD5 из манифеста, а не HTTP-кэш |
| `/wiki/*` | `Cache-Control: no-cache` | Страницы wiki меняются при каждой заливке — нельзя кэшировать «навсегда»; ревалидация по ETag дёшева (304) |

### Let's Encrypt и автопродление

> При `NO_SSL=1` (напр. Cloudflare Tunnel) весь этот раздел не применяется: certbot не ставится,
> сертификат не выдаётся и не продлевается, nginx отдаёт plain HTTP на :80 — TLS терминируется снаружи.

- Выдача — **webroot-режим**: `certbot certonly --webroot -w /var/www/letsencrypt`.
  Челленджи `/.well-known/acme-challenge/*` отдаёт сам контейнер на порту 80, поэтому
  останавливать Nginx не нужно. При первом запуске скрипт временно ставит HTTP-конфиг
  (без TLS), получает сертификат и переключается на финальный конфиг.
- Продление — штатный `certbot.timer` (systemd). После успешного renew выполняется
  `/etc/letsencrypt/renewal-hooks/deploy/reload-fileserver.sh`:
  `docker exec fileserver nginx -s reload`.
- Проверка: `sudo certbot renew --dry-run`.

### SSH / SFTP

- Глобально: `PasswordAuthentication no`, `KbdInteractiveAuthentication no`,
  `PermitRootLogin prohibit-password` (root — только по ключу).
- Для `exporter`: `ChrootDirectory /srv/gamefiles`, `ForceCommand internal-sftp`,
  без TCP/X11-форвардов; shell у пользователя — `/usr/sbin/nologin`.
- Конфиг лежит в drop-in'е `/etc/ssh/sshd_config.d/10-gamefiles.conf` (префикс `10-`
  гарантирует приоритет над другими drop-in'ами, например cloud-init).

## Локальное тестирование (Docker на localhost)

Полный локальный сценарий без VPS: тот же образ `nginx:alpine`, HTTP без TLS
(TLS проверяется чек-листом выше на реальном сервере), та же политика кэширования.

```bash
cd server
./local-test.sh                 # 1) свежая тестовая сборка + контейнер на http://127.0.0.1:8080
                                #    + автоматическая проверка заголовков (PASS/FAIL)
```

Скрипт создаёт в `/tmp/gamefiles-local/www` файлы `client.exe`, `data/level1.pak`,
`data/level2.pak` и корректные `manifest.json`/`version.json` (MD5 считаются на лету).

Дальше — сценарии Launcher (`launcher.json`: `"serverUrl": "http://127.0.0.1:8080"`,
`"gameFolder": "./game"`, `"executable": "client.exe"`):

| # | Действие | Ожидаемый результат |
|---|---|---|
| 1 | Первый запуск Launcher (нет `local_version.json`) | Полное скачивание: 3 файла |
| 2 | Повторный запуск без изменений | «Версии совпадают», игра запускается, скачиваний нет |
| 3 | `./local-test.sh modify data/level1.pak` → запуск Launcher | Скачивается **только** `data/level1.pak` (новая версия) |
| 4 | `./local-test.sh remove data/level2.pak` → запуск Launcher | Лишний файл удалён локально (кроме `excludeFromDelete`) |

```bash
./local-test.sh stop            # остановить контейнер и убрать /tmp/gamefiles-local
```

> Заливка через Exporter на localhost: укажите в `exporter.json` `"sftpHost": "localhost"`
> и локальный SFTP-сервер с тем же chroot (на Linux — тот же sshd-конфиг из
> `sshd_config_snippet`). Проще и достаточно для проверки логики Launcher — просто
> скопировать файлы в mounted volume, что и делает `local-test.sh`.

## Эксплуатация

| Операция | Команда |
|---|---|
| Логи Nginx (access/error) | `docker logs --tail 100 fileserver` |
| Логи SSH/SFTP | `journalctl -u ssh -f` |
| Перезапуск контейнера | `docker restart fileserver` |
| Изменить nginx-конфиг | Править **на хосте** `/etc/nginx/conf.d/fileserver.conf`, затем `docker exec fileserver nginx -t && docker exec fileserver nginx -s reload` (в контейнере том смонтирован read-only) |
| Ручная заливка без Exporter | `sftp exporter@files.example.com` → `put build/client.exe /www/files/`, затем `/www/manifest.json`, **последним** `/www/version.json` |
| Проверить продление сертификата | `sudo certbot renew --dry-run` |
| Часы/таймзона (опционально) | `timedatectl set-timezone Europe/Moscow` |

### Типовые проблемы

| Симптом | Причина / решение |
|---|---|
| `certbot` не может выдать сертификат | A-запись ещё не резолвится на IP VPS (`dig +short <домен>`); порт 80 закрыт провайдером; перезапустите `setup-vps.sh` после исправления DNS |
| `sftp: Could not connect` / `Permission denied (publickey)` | Ключ не добавлен в `/home/exporter/.ssh/authorized_keys` или нарушены права (StrictModes): `.ssh` 700, ключ 600, владелец exporter |
| Потеряли приватный ключ локально | Если есть доступ root на VPS — возьмите с сервера: `sudo cat /root/exporter-sftp-key` (SFTP) или `sudo cat /root/.ssh/id_ed25519` (админский). Без доступа root — создайте новую пару, уберите старый публичный ключ из `authorized_keys`, добавьте новый и перезапустите `setup-vps.sh` |
| sshd пишет `chroot directory ... is not owned by root` | Кто-то изменил владельца/права `/srv/gamefiles` — верните `root:root 755` (`setup-vps.sh` делает это автоматически) |
| Launcher скачивает файл, но MD5 не сходится | Запущен в первые ~5 с после деплоя (окно `open_file_cache_valid`) — ретраи Launcher это перекрывают; если повторяется систематически — проверьте, что заливка завершена (version.json обновлён) |
 | 403 на `/` по HTTPS | Нормально: index-файла нет, листинг каталогов выключен. Нужны только точные URL из манифеста |
