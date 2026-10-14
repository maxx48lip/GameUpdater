#!/usr/bin/env bash
# =============================================================================
# local-test.sh — локальный сценарий тестирования серверной части (Task 3, Этап 6).
#
# Поднимает тот же образ nginx:alpine на localhost (HTTP без TLS) и наполняет
# web-корень тестовой сборкой. Позволяет воспроизвести все сценарии Launcher:
#   1. первый запуск → полное скачивание;
#   2. повторный запуск → «версии совпадают»;
#   3. modify <файл> → новая версия, скачивается только он;
#   4. remove <файл>  → файл удалён из сборки и локально (кроме excludeFromDelete).
#
# Использование:
#   ./local-test.sh                    — свежий сценарий + проверка заголовков
#   ./local-test.sh modify <path> [ver]— изменить файл в сборке, новая версия
#   ./local-test.sh remove  <path> [ver]— удалить файл из сборки, новая версия
#   ./local-test.sh stop               — остановить контейнер и убрать рабочую папку
#
# Путь к файлам — относительный от files/ (как в манифесте): client.exe, data/level1.pak.

set -euo pipefail

PORT="${PORT:-8080}"
WORK=/tmp/gamefiles-local
WEBROOT="$WORK/www"
CONTAINER=gamefiles-local

log()  { printf '\033[1;32m[test]\033[0m %s\n' "$*"; }
warn() { printf '\033[1;33m[test][WARN]\033[0m %s\n' "$*" >&2; }
die()  { printf '\033[1;31m[test][ERROR]\033[0m %s\n' "$*" >&2; exit 1; }

md5_of() {
  if command -v md5sum >/dev/null 2>&1; then md5sum "$1" | awk '{print $1}'
  else md5 -q "$1"; fi
}
size_of() { stat -f%z "$1" 2>/dev/null || stat -c%s "$1"; }

require_docker() {
  command -v docker >/dev/null 2>&1 || die "Docker не найден в PATH."
  docker info >/dev/null 2>&1 || die "Docker-демон недоступен (запустите Docker Desktop / dockerd)."
}

# ------------------------------------------------------------- генерация
make_test_file() { # $1 — путь, $2 — размер в КБ
  local path="$1" kb="$2" i=0
  mkdir -p "$(dirname "$path")"
  while [ "$i" -lt "$kb" ]; do
    printf 'GAMEFILE %s block %d\n' "$(basename "$path")" "$i" >> "$path"
    i=$((i + 1))
  done
}

# Пересчитывает manifest.json и version.json по фактическому содержимому files/.
regenerate_manifest() { # $1 — версия
  local version="$1" entries=() f rel
  while IFS= read -r rel; do
    f="$WEBROOT/files/$rel"
    entries+=("$(printf '    { "path": "%s", "hash": "%s", "size": %s }' \
      "$rel" "$(md5_of "$f")" "$(size_of "$f")")")
  done < <(cd "$WEBROOT/files" && find . -type f | sed 's|^\./||' | LC_ALL=C sort)

  local body="" e
  if [ "${#entries[@]}" -gt 0 ]; then
    for e in "${entries[@]}"; do
      [ -n "$body" ] && body+=$',\n'
      body+="$e"
    done
  fi

  printf '{\n  "version": "%s",\n  "algorithm": "MD5",\n  "files": [\n%s\n  ]\n}\n' \
    "$version" "$body" > "$WEBROOT/manifest.json"

  printf '{\n  "version": "%s",\n  "buildDate": "%s",\n  "minLauncherVersion": "1.0.0"\n}\n' \
    "$version" "$(date -u +%Y-%m-%dT%H:%M:%SZ)" > "$WEBROOT/version.json"

  log "Манифест пересчитан: ${#entries[@]} файлов, версия $version."
}

current_version() {
  sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$WEBROOT/version.json" | head -n1
}

bump_version() {
  local cur major minor patch
  cur="$(current_version)"
  IFS=. read -r major minor patch <<<"$cur"
  # оставляем только цифры (версии имеют суффикс "-local", например 0.0.1-local)
  patch="${patch%%[!0-9]*}"
  printf '%s.%s.%s-local' "${major:-0}" "${minor:-0}" "$(( ${patch:-0} + 1 ))"
}

write_local_nginx_conf() {
  # HTTP-вариант того же конфига (без TLS): те же локации, кэширование и gzip.
  mkdir -p "$WORK/conf.d"
  cat > "$WORK/conf.d/local.conf" <<'EOF'
# Локальный тестовый конфиг (local-test.sh) — HTTP без TLS, правила как в nginx.conf.
server {
    listen 80;
    server_name localhost;

    root /usr/share/nginx/html;

    sendfile on;
    tcp_nopush on;
    # off — чтобы modify/remove отражались мгновенно (в продакшене включён, см. nginx.conf)
    open_file_cache off;

    gzip on;
    gzip_vary on;
    gzip_comp_level 5;
    gzip_min_length 1024;
    gzip_types application/json text/plain text/css application/javascript image/svg+xml;

    location = /version.json {
        add_header Cache-Control "no-cache";
    }

    location = /manifest.json {
        add_header Cache-Control "no-cache";
    }

    location /files/ {
        add_header Cache-Control "public, max-age=31536000, immutable";
    }

    location /wiki/ {
        add_header Cache-Control "no-cache";
    }
}
EOF
}

start_container() {
  docker pull nginx:alpine >/dev/null 2>&1 || warn "Не удалось скачать nginx:alpine — использую кэш."
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d \
    --name "$CONTAINER" \
    -p "127.0.0.1:${PORT}:80" \
    -v "${WEBROOT}:/usr/share/nginx/html:ro" \
    -v "${WORK}/conf.d:/etc/nginx/conf.d:ro" \
    nginx:alpine >/dev/null
  local i
  for i in $(seq 1 30); do
    if [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" = "true" ]; then
      return 0
    fi
    sleep 1
  done
  die "Контейнер $CONTAINER не запустился: $(docker logs --tail 20 "$CONTAINER" 2>&1 | tr '\n' ' ')"
}

# ------------------------------------------------------------------ checks
PASS=0
FAIL=0

check() { # $1 — описание, $2 — условие (0 = успех)
  if [ "$2" -eq 0 ]; then
    printf '  \033[1;32m[PASS]\033[0m %s\n' "$1"
    PASS=$((PASS + 1))
  else
    printf '  \033[1;31m[FAIL]\033[0m %s\n' "$1"
    FAIL=$((FAIL + 1))
  fi
}

run_checks() {
  local base="http://127.0.0.1:${PORT}" h
  log "Проверка заголовков на ${base}..."

  # дождёмся, пока nginx ответит
  local i code=000
  for i in $(seq 1 30); do
    code="$(curl -s -o /dev/null -w '%{http_code}' "${base}/version.json" || true)"
    if [ "$code" = "200" ]; then break; fi
    sleep 1
  done

  h="$(curl -sI "${base}/version.json" || true)"
  check "GET /version.json → 200" $([ "$code" = "200" ]; echo $?)
  check "version.json: Cache-Control: no-cache" \
    "$(printf '%s' "$h" | grep -qi '^cache-control:[[:space:]]*no-cache'; echo $?)"

  h="$(curl -sI "${base}/manifest.json" || true)"
  check "GET /manifest.json → 200 + Cache-Control: no-cache" \
    "$(printf '%s' "$h" | grep -qi '^cache-control:[[:space:]]*no-cache'; echo $?)"

  h="$(curl -sI "${base}/files/client.exe" || true)"
  check "GET /files/client.exe → 200 + Cache-Control: public, max-age=31536000, immutable" \
    "$(printf '%s' "$h" | grep -qi '^cache-control:[[:space:]]*public,[[:space:]]*max-age=31536000,[[:space:]]*immutable'; echo $?)"

  h="$(curl -sI -H 'Accept-Encoding: gzip' "${base}/files/data/level1.pak" || true)"
  check ".pak не сжимается gzip (нет Content-Encoding: gzip)" \
    "$(printf '%s' "$h" | grep -qi '^content-encoding:[[:space:]]*gzip'; [ $? -ne 0 ]; echo $?)"

  h="$(curl -sI "${base}/wiki/" || true)"
  local wiki_code="$(printf '%s\n' "$h" | head -n1 | awk '{print $2}')"
  check "GET /wiki/ → 200 (index.html)" $([ "$wiki_code" = "200" ]; echo $?)
  check "/wiki/: Cache-Control: no-cache" \
    "$(printf '%s' "$h" | grep -qi '^cache-control:[[:space:]]*no-cache'; echo $?)"

  local wiki_page_code="$(curl -s -o /dev/null -w '%{http_code}' "${base}/wiki/pages/a.html" || true)"
  check "GET /wiki/pages/a.html → 200 (относительная перекрёстная ссылка)" \
    $([ "$wiki_page_code" = "200" ]; echo $?)

  local json_ok=1
  if curl -s "${base}/version.json" | grep -q '"version"'; then json_ok=0; fi
  check "version.json — валидный JSON с полем version" "$json_ok"

  printf '\nИтог: %d прошло, %d упало.\n' "$PASS" "$FAIL"
}

# ------------------------------------------------------------------ setup
cmd_setup() {
  require_docker
  if [ -e "$WORK" ]; then
    log "Стираю предыдущую рабочую папку $WORK (свежий сценарий)..."
    docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
    rm -rf "$WORK"
  fi

  mkdir -p "$WEBROOT/files/data"
  log "Генерирую тестовую сборку в $WEBROOT..."
  make_test_file "$WEBROOT/files/client.exe"     200
  make_test_file "$WEBROOT/files/data/level1.pak" 300
  make_test_file "$WEBROOT/files/data/level2.pak" 150
  regenerate_manifest "0.0.1-local"

  # Минимальная статическая wiki с относительными перекрёстными ссылками (как в /wiki/).
  mkdir -p "$WEBROOT/wiki/pages"
  cat > "$WEBROOT/wiki/index.html" <<'EOF'
<!doctype html><html><head><title>Test Wiki</title></head>
<body><a href="pages/a.html">page a</a></body></html>
EOF
  cat > "$WEBROOT/wiki/pages/a.html" <<'EOF'
<!doctype html><html><head><title>Page A</title></head>
<body><a href="../index.html">home</a></body></html>
EOF

  write_local_nginx_conf
  start_container
  run_checks

  cat <<EOF

=============================================================================
 [OK] Локальный сервер поднят: http://127.0.0.1:${PORT}

 launcher.json для сценариев:
   {
     "serverUrl": "http://127.0.0.1:${PORT}",
     "gameFolder": "./game",
     "executable": "client.exe",
     "excludeFromDelete": ["local_version.json"]
   }

 Сценарии (см. Task_3, Этап 6):
   1. Первый запуск Launcher → полное скачивание (3 файла).
   2. Повторный запуск       → «версии совпадают», игра запускается.
   3. ./local-test.sh modify data/level1.pak
      → новый запуск Launcher скачает ТОЛЬКО level1.pak.
   4. ./local-test.sh remove data/level2.pak
      → новый запуск Launcher удалит лишний файл локально.

 Очистка: ./local-test.sh stop
=============================================================================
EOF
}

cmd_modify() {
  local path="${1:-}" version="${2:-}"
  [ -n "$path" ] || die "Укажите путь файла (относительно files/), например data/level1.pak."
  require_docker
  [ -d "$WEBROOT/files" ] || die "Нет рабочей папки — сначала запустите ./local-test.sh"
  local target="$WEBROOT/files/$path"
  [ -f "$target" ] || die "Файл не найден в тестовой сборке: $path"

  # Меняем содержимое (размер + MD5) — имитируем новую версию файла.
  { head -c 4096 /dev/urandom; printf '\nmodified %s\n' "$(date -u)"; } >> "$target"
  [ -n "$version" ] || version="$(bump_version)"
  regenerate_manifest "$version"

  # Контейнер монтирует www read-only, но сам каталог на хосте изменился —
  # nginx подхватит новые файлы без перезапуска (open_file_cache ≤ 30s).
  log "Файл $path изменён. Версия: $(current_version)."
  log "Запустите Launcher повторно — должен скачаться только $path."
}

cmd_remove() {
  local path="${1:-}" version="${2:-}"
  [ -n "$path" ] || die "Укажите путь файла (относительно files/), например data/level2.pak."
  require_docker
  [ -d "$WEBROOT/files" ] || die "Нет рабочей папки — сначала запустите ./local-test.sh"
  local target="$WEBROOT/files/$path"
  [ -f "$target" ] || die "Файл не найден в тестовой сборке: $path"

  rm -f "$target"
  # уберём пустые каталоги
  find "$WEBROOT/files" -depth -type d -empty -delete 2>/dev/null || true
  [ -n "$version" ] || version="$(bump_version)"
  regenerate_manifest "$version"

  log "Файл $path удалён из сборки. Версия: $(current_version)."
  log "Запустите Launcher повторно — файл должен быть удалён локально (кроме excludeFromDelete)."
}

cmd_stop() {
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  rm -rf "$WORK"
  log "Остановлено: контейнер $CONTAINER удалён, $WORK стёрта."
}

# ------------------------------------------------------------------- main
case "${1:-setup}" in
  setup)   cmd_setup ;;
  modify)  shift; cmd_modify "$@" ;;
  remove)  shift; cmd_remove "$@" ;;
  stop)    cmd_stop ;;
  *) die "Неизвестная команда: $1 (доступно: setup, modify <path> [ver], remove <path> [ver], stop)" ;;
esac
