#!/usr/bin/env bash
# =============================================================================
# setup-simple.sh — простая настройка VPS для GameUpdater (Debian 12 / Ubuntu 22+)
#
# В отличие от setup-vps.sh:
#   - НЕ отключает парольную аутентификацию и НЕ создаёт отдельного пользователя
#     exporter — заливка идёт по SFTP от root с паролем (sshpass на клиенте);
#   - Nginx работает напрямую на хосте (не в Docker), отдаёт /srv/gamefiles/www;
#   - HTTPS через Let's Encrypt для $DOMAIN (webroot-режим, как в setup-vps.sh).
#
# Запуск (на VPS, от root):
#   DOMAIN=files.example.com EMAIL=admin@example.com ./setup-simple.sh
#   NO_SSL=1 DOMAIN=files.example.com ./setup-simple.sh    # только plain HTTP
#
# Идемпотентен: повторный запуск безопасен.
# =============================================================================
set -euo pipefail

DOMAIN="${DOMAIN:?Set the DOMAIN env var, e.g. DOMAIN=files.example.com}"
EMAIL="${EMAIL:-}"
NO_SSL="${NO_SSL:-0}"
WEB_ROOT=/srv/gamefiles/www
ACME_WEBROOT=/var/www/letsencrypt
NGINX_SITE="/etc/nginx/sites-available/$DOMAIN.conf"

if [ "$(id -u)" -ne 0 ]; then
    echo "Run as root." >&2
    exit 1
fi

# EMAIL опционален: без него регистрация Let's Encrypt идёт без контактного адреса
# (автопродление при этом работает). Добавить позже: certbot reconfigure -d $DOMAIN --email <addr>

log() { printf '\n\033[1;34m== %s ==\033[0m\n' "$*"; }

# --- 1. Пакеты -----------------------------------------------------------------
log "Packages"
export DEBIAN_FRONTEND=noninteractive
apt-get update -qq
apt-get install -y -qq nginx openssh-server >/dev/null
if [ "$NO_SSL" != "1" ]; then
    apt-get install -y -qq certbot >/dev/null
fi

# Swap как страховка от OOM на малом VPS (только если его нет)
if ! swapon --show | grep -q .; then
    log "Swap 1GB"
    fallocate -l 1G /swapfile
    chmod 600 /swapfile
    mkswap /swapfile >/dev/null
    swapon /swapfile
    grep -q '/swapfile' /etc/fstab || echo '/swapfile none swap sw 0 0' >> /etc/fstab
fi

# --- 2. Каталоги -----------------------------------------------------------------
log "Directories"
mkdir -p "$WEB_ROOT" "$ACME_WEBROOT"

# --- 3. Nginx --------------------------------------------------------------------
# HTTP-only сайт: ACME-челленджи + (редирект на HTTPS | отдача статики при NO_SSL=1).
write_http_site() {
    if [ "$NO_SSL" = "1" ]; then
        cat > "$NGINX_SITE" <<'EOF'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name __DOMAIN__;

    root /srv/gamefiles/www;
    index index.html;

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
    }

    location / {
        try_files $uri $uri/ =404;
    }
}
EOF
    else
        cat > "$NGINX_SITE" <<'EOF'
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name __DOMAIN__;

    location ^~ /.well-known/acme-challenge/ {
        root /var/www/letsencrypt;
    }

    location / {
        return 301 https://$host$request_uri;
    }
}
EOF
    fi
    sed -i "s/__DOMAIN__/$DOMAIN/" "$NGINX_SITE"
}

# Финальный сайт: HTTP (ACME + редирект) и HTTPS со статикой.
write_https_site() {
    cat > "$NGINX_SITE" <<EOF
# --- HTTP: ACME-челленджи Let's Encrypt + редирект на HTTPS -------------------
server {
    listen 80 default_server;
    listen [::]:80 default_server;
    server_name $DOMAIN;

    location ^~ /.well-known/acme-challenge/ {
        root $ACME_WEBROOT;
    }

    location / {
        return 301 https://\$host\$request_uri;
    }
}

# --- HTTPS: статика для автообновления игры + страница скачивания лаунчера ----
server {
    listen 443 ssl http2;
    listen [::]:443 ssl http2;
    server_name $DOMAIN;

    root $WEB_ROOT;
    index index.html;

    ssl_certificate     /etc/letsencrypt/live/$DOMAIN/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/$DOMAIN/privkey.pem;
    ssl_protocols TLSv1.2 TLSv1.3;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;

    # --- Производительность (малый VPS) ----------------------------------------
    sendfile on;
    tcp_nopush on;
    # Короткое valid-окно намеренно: после деплоя файл из files/* может отдаваться
    # по старому fd до open_file_cache_valid. Launcher сверяет MD5 и повторяет
    # скачивание (до 3 раз), поэтому окно перекрывается ретраями.
    open_file_cache max=2048 inactive=30s;
    open_file_cache_valid 5s;
    open_file_cache_min_uses 1;
    open_file_cache_errors on;

    # --- Сжатие: только текстовые типы (.zip/.pak уже сжаты) --------------------
    gzip on;
    gzip_vary on;
    gzip_comp_level 5;
    gzip_min_length 1024;
    gzip_types application/json text/plain text/css application/javascript image/svg+xml;

    # --- Кэширование метаданных: всегда сверять с сервером ----------------------
    location = /version.json {
        add_header Cache-Control "no-cache";
    }

    location = /manifest.json {
        add_header Cache-Control "no-cache";
    }

    # --- Игровые файлы: кэш навсегда (целостность — по MD5 из манифеста) --------
    location /files/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }

    # --- Остальное (index.html, zip лаунчера): ревалидация при каждом запросе ---
    location / {
        add_header Cache-Control "no-cache";
    }
}
EOF
}

nginx_ctl() { nginx -t && (systemctl reload nginx 2>/dev/null || systemctl restart nginx); }

log "Nginx site"
ln -sf "$NGINX_SITE" "/etc/nginx/sites-enabled/$(basename "$NGINX_SITE")"
rm -f /etc/nginx/sites-enabled/default   # не отдавать дефолтную страницу на :80

if [ "$NO_SSL" = "1" ]; then
    write_http_site
else
    if [ -f "/etc/letsencrypt/live/$DOMAIN/fullchain.pem" ]; then
        log "Certificate exists — final config"
        write_https_site
    else
        log "Issuing Let's Encrypt certificate (webroot)"
        # Временный HTTP-конфиг: челленджи /.well-known/acme-challenge/ отдаёт сам nginx.
        write_http_site
        nginx_ctl
        if [ -n "$EMAIL" ]; then
            EMAIL_ARGS=(--email "$EMAIL")
        else
            EMAIL_ARGS=(--register-unsafely-without-email)
        fi
        certbot certonly --webroot -w "$ACME_WEBROOT" -d "$DOMAIN" \
            "${EMAIL_ARGS[@]}" --agree-tos --no-eff-email --non-interactive
        # Автопродление: после renew перезагружаем nginx.
        mkdir -p /etc/letsencrypt/renewal-hooks/deploy
        printf '#!/bin/sh\nsystemctl reload nginx 2>/dev/null || systemctl restart nginx\n' \
            > "/etc/letsencrypt/renewal-hooks/deploy/reload-$DOMAIN.sh"
        chmod +x "/etc/letsencrypt/renewal-hooks/deploy/reload-$DOMAIN.sh"
        write_https_site
    fi
fi

nginx_ctl

# --- 4. SSH/SFTP -------------------------------------------------------------------
log "SSH/SFTP"
SSHD=/etc/ssh/sshd_config
sed -ri 's/^#?PasswordAuthentication.*/PasswordAuthentication yes/' "$SSHD"
sed -ri 's/^#?PermitRootLogin.*/PermitRootLogin yes/' "$SSHD"
grep -Eq '^Subsystem[[:space:]]+sftp' "$SSHD" || echo 'Subsystem sftp internal-sftp' >> "$SSHD"
sshd -t && (systemctl reload ssh 2>/dev/null || systemctl restart ssh)

# --- 5. Firewall ---------------------------------------------------------------------
log "Firewall"
if command -v ufw >/dev/null 2>&1 && ufw status 2>/dev/null | grep -q 'Status: active'; then
    ufw allow OpenSSH >/dev/null
    ufw allow 80/tcp >/dev/null
    if [ "$NO_SSL" != "1" ]; then
        ufw allow 443/tcp >/dev/null
    fi
fi

# --- Готово ---------------------------------------------------------------------------
log "Done"
echo "Web root : $WEB_ROOT"
if [ "$NO_SSL" = "1" ]; then
    echo "Served at: http://$DOMAIN/ (plain HTTP)"
else
    echo "Served at: https://$DOMAIN/"
    # Автопродление: certbot.timer запускает "certbot renew" 2 раза в сутки;
    # при продлении срабатывает renewal-hook, перезагружающий nginx.
    systemctl is-active certbot.timer >/dev/null 2>&1 || systemctl enable --now certbot.timer
    echo "Auto-renew: $(systemctl list-timers certbot.timer --no-pager --no-legend | head -1)"
fi
echo "SFTP     : root@<ip> — парольная аутентификация включена"
