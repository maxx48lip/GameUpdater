#!/usr/bin/env bash
# =============================================================================
# deploy.sh — деплой сборки игры + лаунчера на VPS-файловый сервер
#
# Запуск с этой машины:
#   ./deploy.sh [version]        # настройки берутся из .env (шаблон — example.env)
# Значения задаются в .env; при отсутствии файла их можно передать env-переменными:
#   SFTP_PASSWORD='...' VPS_HOST=files.example.com DOMAIN=files.example.com \
#       EMAIL=you@example.com ./deploy.sh [version]
#
#   - version по умолчанию 1.0.0
#   - HTTPS для $DOMAIN включён всегда; EMAIL опционален
#     (без него регистрация Let's Encrypt идёт без контактного адреса)
#   - exporter.json — скопируйте из exporter.example.json и заполните своими значениями
#     (реальный файл с адресами в git не попадает, см. .gitignore)
#
# Шаги: настройка VPS (идемпотентно) → сборка лаунчера win-x64 → заливка
# index.html + zip лаунчера → Exporter (files/ → manifest.json → version.json).
# =============================================================================
set -euo pipefail
cd "$(dirname "$0")"

# Локальные настройки из .env (gitignored, шаблон — example.env).
if [ -f .env ]; then
    set -a
    # shellcheck disable=SC1091
    . ./.env
    set +a
fi

HOST="${VPS_HOST:?Set the VPS_HOST env var or put it in .env (IP or hostname of the file server)}"
SSH_USER="${SSH_USER:-root}"
DOMAIN="${DOMAIN:?Set the DOMAIN env var or put it in .env (domain for HTTPS, e.g. files.example.com)}"
VERSION=${1:-1.0.0}
EMAIL=${EMAIL:-}      # опционально: контактный адрес Let's Encrypt
NO_SSL=${NO_SSL:-0}   # 1 = plain HTTP (не рекомендуется; HTTPS — по умолчанию)

[ -f exporter.json ] || { echo "exporter.json not found — copy exporter.example.json to exporter.json and fill in your values." >&2; exit 1; }

: "${SFTP_PASSWORD:?Set the SFTP_PASSWORD env var or put it in .env (VPS password)}"
export SSHPASS="$SFTP_PASSWORD"   # sshpass -e читает пароль из SSHPASS

command -v sshpass >/dev/null 2>&1 || { echo "sshpass not found: brew install hudochenkov/sshpass/sshpass" >&2; exit 1; }
SSH_OPTS="-o StrictHostKeyChecking=accept-new -o ConnectTimeout=10"
WEB_ROOT=/srv/gamefiles/www   # должно совпадать с setup-simple.sh и exporter.json

# --- 1. Настройка VPS (nginx + TLS + sshd), идемпотентно ----------------------
echo "== [1/4] Setting up VPS ($DOMAIN, NO_SSL=$NO_SSL)"
sshpass -e ssh $SSH_OPTS "$SSH_USER@$HOST" \
    "DOMAIN='$DOMAIN' EMAIL='$EMAIL' NO_SSL='$NO_SSL' bash -s" < ../server/setup-simple.sh

# --- 2. Сборка лаунчера (win-x64, self-contained single file) ------------------
echo "== [2/4] Building launcher win-x64"
STAGE=stage/launcher-win-x64
rm -rf stage
mkdir -p "$STAGE"
dotnet publish ../GameUpdater/src/GameUpdater.Launcher \
    -c Release -r win-x64 --self-contained true --nologo \
    -p:PublishSingleFile=true -p:DebugType=embedded -p:PublishTrimmed=true \
    -o "$STAGE"
mv "$STAGE/GameUpdater.Launcher.exe" "$STAGE/launcher.exe"

if [ "$NO_SSL" = "1" ]; then SERVER_URL="http://$DOMAIN"; else SERVER_URL="https://$DOMAIN"; fi
cat > "$STAGE/launcher.json" <<EOF
{
  "serverUrl": "$SERVER_URL",
  "gameFolder": "./game",
  "executable": "client_prototype.exe",
  "excludeFromDelete": ["local_version.json", "settings.json"]
}
EOF

(cd stage && rm -f ../launcher-win-x64.zip && zip -qr ../launcher-win-x64.zip launcher-win-x64)
ls -lh launcher-win-x64.zip

# --- 3. Заливка веб-ассетов (index.html + zip лаунчера) ------------------------
echo "== [3/4] Uploading index.html and launcher"
# BSD mktemp requires the X's to be at the end of the template (no suffix after them).
BATCH=$(mktemp "${TMPDIR:-/tmp}/gameup-web.XXXX")
{
    echo "put index.html $WEB_ROOT/index.html"
    echo "put launcher-win-x64.zip $WEB_ROOT/launcher-win-x64.zip"
} > "$BATCH"
# -o BatchMode=no: "-b" implies batch mode, which disables the password prompt.
sshpass -e sftp $SSH_OPTS -o BatchMode=no -b "$BATCH" "$SSH_USER@$HOST"
rm -f "$BATCH"

# --- 4. Заливка сборки игры через Exporter --------------------------------------
echo "== [4/4] Uploading game build v$VERSION (Exporter)"
SFTP_PASSWORD="$SFTP_PASSWORD" dotnet run --project ../GameUpdater/src/GameUpdater.Exporter -c Release \
    -- --version "$VERSION" --config exporter.json

# --- Проверка --------------------------------------------------------------------
echo "== Verify"
curl -fsS "$SERVER_URL/version.json" && echo
echo "Done: $SERVER_URL/ (index.html + launcher-win-x64.zip), game files at /files/"
