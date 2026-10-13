#!/usr/bin/env bash
# =============================================================================
# setup-vps.sh — первичная настройка VPS (Debian 12 x64) для автообновления игры.
#
# Что делает:
#   1. Обновляет систему; ставит openssh-server, ufw, docker-ce, certbot.
#   2. Создаёт SFTP-пользователя exporter и структуру каталогов:
#        /srv/gamefiles          — chroot-корень (root:root 755, требование OpenSSH)
#        /srv/gamefiles/www      — записываемая область (exporter:exporter 755),
#                                  она же web-корень Nginx
#   3. Харденинг SSH: только ключи; root — без пароля; chroot для exporter.
#   4. UFW: открыты только 22, 80, 443.
#   5. Nginx в Docker (контейнер fileserver) + Let's Encrypt (webroot через контейнер).
#
# Использование (на VPS):
#   DOMAIN=files.example.com EMAIL=admin@example.com sudo ./setup-vps.sh
#
# Ключи: если ROOT_PUBKEY/EXPORTER_PUBKEY не заданы, скрипт сам создаёт пары
# ed25519 (если их ещё нет), ставит публичные части на сервер и выводит приватные
# в консоль — сохраните их. Места хранения на сервере:
#   /root/.ssh/id_ed25519    — админский ключ (ssh root@<домен>)
#   /root/exporter-sftp-key  — SFTP-ключ для Exporter (sftpKeyPath в exporter.json)
#
# Необязательные переменные (свои ключи вместо автогенерации):
#   ROOT_PUBKEY      Публичный ключ администратора (строка ssh-ed25519 ... или путь к .pub).
#   EXPORTER_PUBKEY  Публичный ключ для SFTP-заливки (строка или путь к .pub).
# Ключ может быть передан строкой ("ssh-ed25519 AAAA...") или путём к .pub файлу.
#
# Важно: если скрипт запускается в CI/пайплайне, где логируется вывод, — передайте
# свои ключи через переменные, чтобы не публиковать сгенерированные приватные.
#
# Скрипт идемпотентен: повторный запуск безопасен (контейнер fileserver пересоздаётся,
# конфиги перезаписываются тем же содержимым, ключи не дублируются и не перегенерируются).

set -euo pipefail

# ------------------------------------------------------------------- helpers
log()  { printf '\033[1;32m[setup]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[setup][WARN]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[setup][ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

usage() {
  cat <<'EOF'
Использование:
  DOMAIN=<домен> EMAIL=<email> [ROOT_PUBKEY=...] [EXPORTER_PUBKEY=...] sudo ./setup-vps.sh

Переменные окружения:
  DOMAIN           Обязательна. Домен, на который указывает A-запись (files.example.com).
  EMAIL            Обязателен. Email для аккаунта Let's Encrypt.
  ROOT_PUBKEY      Необязательно. Свой публичный ключ админа → /root/.ssh/authorized_keys.
  EXPORTER_PUBKEY  Необязательно. Свой публичный ключ SFTP-заливки → /home/exporter/.ssh/authorized_keys.

Если ключи не переданы, скрипт сам создаёт пары ed25519 (если их ещё нет),
устанавливает публичные части и выводит приватные в консоль:
  /root/.ssh/id_ed25519    — для ssh root@<домен>
  /root/exporter-sftp-key  — sftpKeyPath в exporter.json

Ключ может быть передан строкой ("ssh-ed25519 AAAA...") или путём к .pub файлу.
EOF
}

if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
  usage
  exit 0
fi

# ----------------------------------------------------------------- preflight
[ "$(id -u)" -eq 0 ] || die "Запустите от root: sudo DOMAIN=... EMAIL=... $0"

. /etc/os-release
case "${ID:-}" in
  debian) ;;
  *) die "Скрипт рассчитан на Debian (обнаружено: ${ID:-unknown})" ;;
esac
if [ "${VERSION_ID:-}" != "12" ]; then
  warn "Ожидался Debian 12, обнаружен Debian ${VERSION_ID}. Продолжаем, но поведение не гарантировано."
fi
[ "$(dpkg --print-architecture)" = "amd64" ] \
  || die "Ожидалась архитектура amd64 (x64), обнаружено: $(dpkg --print-architecture)"

DOMAIN="${DOMAIN:-}"
EMAIL="${EMAIL:-}"
ROOT_PUBKEY="${ROOT_PUBKEY:-}"
EXPORTER_PUBKEY="${EXPORTER_PUBKEY:-}"

[ -n "$DOMAIN" ] || { usage; die "Не задан DOMAIN"; }
[ -n "$EMAIL" ]  || { usage; die "Не задан EMAIL"; }
printf '%s' "$DOMAIN" | grep -Eqi '^[a-z0-9]([a-z0-9.-]*[a-z0-9])?(\.[a-z]{2,})+$' \
  || die "DOMAIN не похож на домен: '$DOMAIN'"

export DEBIAN_FRONTEND=noninteractive

if ! getent hosts "$DOMAIN" >/dev/null 2>&1; then
  warn "Домен $DOMAIN пока не резолвится. Let's Encrypt не сможет выдать сертификат."
  warn "Создайте A-запись на IP этого VPS и перезапустите скрипт (он идемпотентен)."
fi

# ------------------------------------------------- 1. система и пакеты
log "Обновление системы..."
apt-get update -qq
if ! apt-get -y -qq upgrade; then
  warn "apt-get upgrade завершился с ошибкой — проверьте вручную, продолжаем."
fi

# Swap: страховка от OOM на 750 МБ RAM во время apt/upgrade и docker pull.
if [ "$(swapon --show=NAME | wc -l)" -eq 0 ] && [ ! -f /swapfile ]; then
  log "Создаю swap-файл 1 ГБ (swap не найден)..."
  fallocate -l 1G /swapfile || dd if=/dev/zero of=/swapfile bs=1M count=1024 status=none
  chmod 600 /swapfile
  mkswap /swapfile >/dev/null
  swapon /swapfile
  grep -q '^/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

log "Добавляю репозиторий Docker CE..."
apt-get install -y -qq ca-certificates curl gnupg >/dev/null
install -m 0755 -d /etc/apt/keyrings
if [ ! -f /etc/apt/keyrings/docker.asc ]; then
  curl -fsSL https://download.docker.com/linux/debian/gpg -o /etc/apt/keyrings/docker.asc
fi
chmod a+r /etc/apt/keyrings/docker.asc
echo "deb [arch=$(dpkg --print-architecture) signed-by=/etc/apt/keyrings/docker.asc] \
https://download.docker.com/linux/debian ${VERSION_CODENAME} stable" > /etc/apt/sources.list.d/docker.list
apt-get update -qq

log "Установка пакетов: docker-ce, openssh-server, ufw, certbot..."
apt-get install -y -qq \
  docker-ce docker-ce-cli containerd.io docker-buildx-plugin docker-compose-plugin \
  openssh-server ufw certbot >/dev/null

# Лог-ротация контейнеров (nginx пишет access log в stdout → docker logs).
if [ ! -f /etc/docker/daemon.json ]; then
  mkdir -p /etc/docker
  cat > /etc/docker/daemon.json <<'EOF'
{
  "log-driver": "json-file",
  "log-opts": { "max-size": "10m", "max-file": "3" }
}
EOF
fi

systemctl enable --now docker
systemctl enable --now ssh
systemctl enable certbot.timer || true

# ------------------------------------------- 2. пользователь и каталоги
log "Создаю пользователя exporter и структуру /srv/gamefiles..."
if ! id -u exporter >/dev/null 2>&1; then
  useradd --create-home --shell /usr/sbin/nologin \
    --comment "SFTP-only uploader (game files)" exporter
else
  # Идемпотентно фиксируем: shell запрещён.
  usermod -s /usr/sbin/nologin exporter
fi

# Chroot-корень обязан принадлежать root и не быть записываемым никем (проверка OpenSSH).
mkdir -p /srv/gamefiles/www/files
chown root:root /srv /srv/gamefiles
chmod 755 /srv /srv/gamefiles
# Единственная записываемая область — web-корень Nginx.
chown exporter:exporter /srv/gamefiles/www /srv/gamefiles/www/files
chmod 755 /srv/gamefiles/www /srv/gamefiles/www/files

# Webroot для ACME HTTP-01 челленджей Let's Encrypt (отдаётся контейнером на :80).
mkdir -p /var/www/letsencrypt/.well-known/acme-challenge
chown -R root:root /var/www/letsencrypt
chmod 755 /var/www/letsencrypt /var/www/letsencrypt/.well-known \
          /var/www/letsencrypt/.well-known/acme-challenge

# ------------------------------------------------------------------- keys
install_authorized_key() {
  # $1 — путь к authorized_keys, $2 — владелец, $3 — ключ (строка или путь к файлу)
  local ak_path="$1" owner="$2" key_content="$3" ssh_dir
  [ -n "$key_content" ] || return 0
  if [ -f "$key_content" ]; then
    key_content="$(head -n1 "$key_content")"
  fi
  case "$key_content" in
    ssh-*|ecdsa-*) ;;
    *) die "Не похоже на публичный SSH-ключ: ${key_content:0:40}..." ;;
  esac
  ssh_dir="$(dirname "$ak_path")"
  mkdir -p "$ssh_dir"
  touch "$ak_path"
  if ! grep -qxF "$key_content" "$ak_path"; then
    printf '%s\n' "$key_content" >> "$ak_path"
    log "Добавлен ключ в $ak_path"
  fi
  chown -R "${owner}:${owner}" "$ssh_dir"
  chmod 700 "$ssh_dir"
  chmod 600 "$ak_path"
}

# Создаёт пару ed25519, если её ещё нет. Печатает NEW / EXISTING в stdout.
ensure_keypair() { # $1 — путь к приватному ключу, $2 — комментарий (метка)
  local priv="$1" comment="$2"
  if [ -f "$priv" ]; then
    echo "EXISTING"
    return 0
  fi
  mkdir -p "$(dirname "$priv")"
  ssh-keygen -q -t ed25519 -N '' -C "$comment" -f "$priv"
  chmod 600 "$priv"
  echo "NEW"
}

# Показывает приватный ключ только при первом создании (повторно — путь к нему).
show_key_if_new() { # $1 — NEW/EXISTING, $2 — путь к приватному ключу, $3 — назначение
  local state="$1" priv="$2" what="$3"
  if [ "$state" = "NEW" ]; then
    warn "$what: ключ создан. Сохраните приватный ключ (повторно показывать не буду):"
    echo "----- BEGIN PRIVATE KEY ($priv) -----"
    cat "$priv"
    echo "----- END PRIVATE KEY -----"
  else
    log "$what: ключ уже существовал — $priv (просмотр: sudo cat $priv)"
  fi
}

# --- Админский ключ (root): доступ по ssh после отключения входа по паролю -----
ROOT_KEY_PRIV=/root/.ssh/id_ed25519
if [ -n "$ROOT_PUBKEY" ]; then
  install_authorized_key /root/.ssh/authorized_keys root "$ROOT_PUBKEY"
else
  key_state="$(ensure_keypair "$ROOT_KEY_PRIV" "gamefiles-admin")"
  install_authorized_key /root/.ssh/authorized_keys root "${ROOT_KEY_PRIV}.pub"
  show_key_if_new "$key_state" "$ROOT_KEY_PRIV" "Ключ администратора (ssh root@${DOMAIN})"
fi

# --- SFTP-ключ (exporter): заливка из Exporter ---------------------------------
EXPORTER_KEY_PRIV=/root/exporter-sftp-key
if [ -n "$EXPORTER_PUBKEY" ]; then
  install_authorized_key /home/exporter/.ssh/authorized_keys exporter "$EXPORTER_PUBKEY"
else
  key_state="$(ensure_keypair "$EXPORTER_KEY_PRIV" "gamefiles-exporter")"
  install_authorized_key /home/exporter/.ssh/authorized_keys exporter "${EXPORTER_KEY_PRIV}.pub"
  show_key_if_new "$key_state" "$EXPORTER_KEY_PRIV" "SFTP-ключ для Exporter (sftpKeyPath)"
fi

# ------------------------------------------------------- 3. SSH харденинг
log "Устанавливаю конфигурацию sshd (только ключи + chroot для exporter)..."
mkdir -p /etc/ssh/sshd_config.d
cat > /etc/ssh/sshd_config.d/10-gamefiles.conf <<'EOF'
# Установлено setup-vps.sh — SFTP-сервер автообновления игры.
# Файл сортируется первым в sshd_config.d → его значения имеют приоритет
# (sshd использует первое встреченное значение директивы).

# Только ключевая аутентификация; root — без пароля.
PasswordAuthentication no
KbdInteractiveAuthentication no
PubkeyAuthentication yes
PermitRootLogin prohibit-password

# SFTP-пользователь exporter: chroot + только internal-sftp, без shell/форвардов.
Match User exporter
    ChrootDirectory /srv/gamefiles
    ForceCommand internal-sftp
    AllowTcpForwarding no
    X11Forwarding no
EOF
chmod 644 /etc/ssh/sshd_config.d/10-gamefiles.conf

sshd -t || die "Конфигурация sshd не прошла проверку (sshd -t)"
systemctl reload ssh

# -------------------------------------------------------------------- UFW
log "Настраиваю UFW (22, 80, 443)..."
ufw allow 22/tcp  >/dev/null
ufw allow 80/tcp  >/dev/null
ufw allow 443/tcp >/dev/null
ufw default deny incoming  >/dev/null
ufw default allow outgoing >/dev/null
if ! ufw --force enable >/dev/null; then
  warn "Не удалось включить UFW — проверьте вручную (ufw status)."
fi

# ------------------------------------------------------- 4. Nginx + Docker
NGINX_CONF=/etc/nginx/conf.d/fileserver.conf
mkdir -p /etc/nginx/conf.d

write_nginx_final() {
  cat > "$NGINX_CONF" <<EOF
# =============================================================================
# fileserver — статика для автообновления игры (установлено setup-vps.sh)
# Домен: ${DOMAIN}
# =============================================================================

# --- HTTP: редирект на HTTPS + ACME-челленджи Let's Encrypt (webroot) -------
server {
    listen 80;
    server_name ${DOMAIN};

    # certbot --webroot кладёт файлы сюда; контейнер отдаёт их наружу.
    location ^~ /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
    }

    location / {
        return 301 https://\$host\$request_uri;
    }
}

# --- HTTPS: отдача файлов ----------------------------------------------------
server {
    listen 443 ssl;
    http2 on;
    server_name ${DOMAIN};

    root /usr/share/nginx/html;   # = /srv/gamefiles/www (read-only volume)

    ssl_certificate     /etc/letsencrypt/live/${DOMAIN}/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/${DOMAIN}/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;

    # --- Производительность (VPS: 750 МБ RAM / 1 ядро) -----------------------
    sendfile on;
    tcp_nopush on;
    # Короткое valid-окно намеренно: после заливки новой версии файлы из files/*
    # могут отдаваться по старому fd до open_file_cache_valid. Launcher сверяет MD5
    # и повторяет скачивание (до 3 раз), поэтому окно перекрывается ретраями.
    open_file_cache max=2048 inactive=30s;
    open_file_cache_valid 5s;
    open_file_cache_min_uses 1;
    open_file_cache_errors on;

    # --- Сжатие ---------------------------------------------------------------
    # gzip только для текстовых типов. .zip / .pak / .gz уже сжаты — их MIME-типы
    # (application/zip, application/gzip, application/octet-stream) в список
    # gzip_types НЕ входят, поэтому к ним gzip не применяется.
    gzip on;
    gzip_vary on;
    gzip_comp_level 5;
    gzip_min_length 1024;
    gzip_types application/json text/plain text/css application/javascript image/svg+xml;

    # --- Кэширование метаданных: всегда сверять с сервером --------------------
    location = /version.json {
        add_header Cache-Control "no-cache";
    }

    location = /manifest.json {
        add_header Cache-Control "no-cache";
    }

    # --- Игровые файлы: кэш навсегда (целостность проверяется по MD5 из манифеста)
    location /files/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }
}
EOF
}

write_nginx_bootstrap() {
  # Временный конфиг до выдачи сертификата: ACME-челленджи + заглушка.
  cat > "$NGINX_CONF" <<EOF
# Временный конфиг (до выдачи Let's Encrypt) — установлено setup-vps.sh.
server {
    listen 80;
    server_name ${DOMAIN};

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
    }

    location / {
        default_type text/plain;
        return 200 "fileserver: сертификат ещё не выдан, повторите setup-vps.sh\n";
    }
}
EOF
}

start_fileserver() {
  docker pull nginx:alpine >/dev/null 2>&1 \
    || warn "Не удалось скачать nginx:alpine — использую кэшированный образ."
  # Пересоздание = детерминированная идемпотентность (конфиги смонтированы ro).
  docker rm -f fileserver >/dev/null 2>&1 || true
  docker run -d \
    --name fileserver \
    --restart unless-stopped \
    -p 80:80 -p 443:443 \
    -v /srv/gamefiles/www:/usr/share/nginx/html:ro \
    -v /etc/letsencrypt:/etc/letsencrypt:ro \
    -v /var/www/letsencrypt:/var/www/letsencrypt:ro \
    -v /etc/nginx/conf.d:/etc/nginx/conf.d:ro \
    nginx:alpine >/dev/null
  local i
  for i in $(seq 1 30); do
    if [ "$(docker inspect -f '{{.State.Running}}' fileserver 2>/dev/null)" = "true" ]; then
      return 0
    fi
    sleep 1
  done
  die "Контейнер fileserver не запустился: $(docker logs --tail 20 fileserver 2>&1 | tr '\n' ' ')"
}

log "Запускаю Nginx в Docker (fileserver)..."
CERT_DIR="/etc/letsencrypt/live/${DOMAIN}"
if [ -f "${CERT_DIR}/fullchain.pem" ]; then
  log "Сертификат для ${DOMAIN} уже есть — пропускаю выдачу."
  write_nginx_final
else
  log "Первичная выдача сертификата Let's Encrypt (webroot через контейнер)..."
  write_nginx_bootstrap
  start_fileserver
  if ! certbot certonly --webroot -w /var/www/letsencrypt \
      -d "$DOMAIN" \
      --email "$EMAIL" --agree-tos --no-eff-email --non-interactive; then
    die "Не удалось получить сертификат. Проверьте: A-запись ${DOMAIN} → IP VPS (dig +short ${DOMAIN}), порты 80/443 открыты, домен делегирован."
  fi
fi

write_nginx_final
start_fileserver

# Автопродление: после успешного renew перезагружаем nginx в контейнере.
mkdir -p /etc/letsencrypt/renewal-hooks/deploy
cat > /etc/letsencrypt/renewal-hooks/deploy/reload-fileserver.sh <<'EOF'
#!/bin/sh
docker exec fileserver nginx -s reload || docker restart fileserver
EOF
chmod 755 /etc/letsencrypt/renewal-hooks/deploy/reload-fileserver.sh

# ------------------------------------------------------------- проверка
log "Финальная проверка..."
docker ps --filter name=fileserver --format 'table {{.Names}}\t{{.Status}}'
http_code="$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1/ || true)"
[ "$http_code" != "000" ] || die "Nginx не отвечает на :80 (docker logs fileserver)"
if [ -f "${CERT_DIR}/fullchain.pem" ]; then
  https_code="$(curl -sk -o /dev/null -w '%{http_code}' https://127.0.0.1/ || true)"
  case "$https_code" in
    4*|5*) log "TLS на :443 работает (HTTP $https_code — до первого деплоя это нормально)." ;;
    *) die "Неожиданный ответ HTTPS: $https_code" ;;
  esac
fi

cat <<EOF

=============================================================================
 [OK] Настройка завершена.

 Домен:        ${DOMAIN}
 SFTP:         exporter@${DOMAIN} (chroot /srv/gamefiles, запись в www/)
 Web-корень:   /srv/gamefiles/www → https://${DOMAIN}/version.json и т.д.

 Дальше:
   1. Сохраните приватные ключи (созданные в этом запуске показаны выше):
        - ${ROOT_KEY_PRIV}      → на вашу машину, для ssh root@${DOMAIN}
        - ${EXPORTER_KEY_PRIV}  → в sftpKeyPath конфига Exporter (на машине/CI)
      Если ключи уже существовали — их пути указаны выше (sudo cat <путь>).
   2. Первый деплой через Exporter с remoteBasePath = "/www"
      (пути в sftp — абсолютные ВНУТРИ chroot, см. README).
   3. Проверьте чек-лист из README (curl version.json/manifest.json/files/*).

 Повторный запуск скрипта безопасен (идемпотентность): ключи не перегенерируются.
=============================================================================
EOF
