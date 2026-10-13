# GameUpdater.Launcher

Консольное приложение для игроков (.NET 10, без NuGet-пакетов): проверяет версию по HTTPS,
сравнивает локальные файлы с манифестом, скачивает изменённые/отсутствующие, удаляет лишние,
запускает игру и завершается.

Целевые ОС: Windows 10+, Linux (x64/arm64), macOS 12+ (Intel и Apple Silicon).

## Сборка

```bash
dotnet build GameUpdater.sln -c Release
# или self-contained бинарь под конкретную платформу:
dotnet publish src/GameUpdater.Launcher -c Release -r osx-arm64 --self-contained true
```

## Конфигурация (launcher.json)

По умолчанию читается **рядом с exe** (`AppContext.BaseDirectory/launcher.json`),
переопределяется флагом `--config <file>`. Пример копируется в папку сборки автоматически.

```json
{
  "serverUrl": "https://files.example.com",
  "gameFolder": "./game",
  "executable": "client.exe",
  "excludeFromDelete": ["local_version.json", "user.cfg", "logs/*"]
}
```

| Поле | Описание |
|---|---|
| `serverUrl` | Базовый URL сервера (http/https). Запрашиваются `{serverUrl}/version.json`, `/manifest.json`, `/files/<путь>` |
| `gameFolder` | Папка с игрой. Относительный путь — от текущей рабочей директории; `~` раскрывается. Создаётся автоматически при первом запуске |
| `executable` | Исполняемый файл игры относительно `gameFolder`. На macOS может быть `.app`-бандлом (запускается бинарь из `Contents/MacOS`) |
| `excludeFromDelete` | Файлы/каталоги, которые не удаляются при очистке «лишних». Паттерны: точное имя файла; имя каталога (исключает всё дерево); глобы — `*` (кроме `/`), `?`, `**` (включая `/`). Без учёта регистра. `local_version.json` исключается всегда |

## Использование

```bash
launcher [--config <file>] [options]
```

| Аргумент | Описание |
|---|---|
| `--config <file>` | Конфиг (по умолчанию `launcher.json` рядом с exe) |
| `--no-launch` | Только обновление, без запуска игры |
| `--no-color` | Отключить ANSI-цвета |
| `-h`, `--help` | Справка |

Коды выхода: `0` — успех, `1` — ошибка (сервер недоступен после отказа от ретрая,
сбой скачивания, нет исполняемого файла и т.п.), `2` — неверные аргументы, `130` — Ctrl+C.

## Алгоритм

1. Чтение `launcher.json` → `local_version.json` в gameFolder (нет файла → полное обновление).
2. Запрос `{serverUrl}/version.json`. Сервер недоступен → `[ERROR] Cannot reach server. Retry? [Y/n]`.
3. Версии совпадают → запуск игры, выход. Иначе — скачивание `manifest.json` и диф:
   - файла нет локально → скачать;
   - размер отличается → скачать;
   - размер совпадает → MD5, не совпал → скачать.
4. Скачивание 3 потоками (`SemaphoreSlim`): во временный `.tmp`, буферы по 80 КБ
   (`HttpCompletionOption.ResponseHeadersRead`), проверка MD5, атомарный `File.Move(tmp, target)`.
   Неверный хеш/сбой сети → повтор до 3 раз, затем `[ERROR] Failed to download <file>.`
5. Удаление локальных файлов, которых нет в манифесте (с учётом `excludeFromDelete`).
6. Запись новой версии в `local_version.json`.
7. Запуск игры: Windows — `Process.Start(exe)`; Linux/macOS — `chmod +x`
   (`File.SetUnixFileMode`, 0755) и запуск по полному пути; macOS `.app` — бинарь из `Contents/MacOS`.

Ctrl+C (`Console.CancelKeyPress`) → отмена загрузок, удаление всех `.tmp`, код выхода **130**.

## Пример вывода

```text
[INFO] Launcher v1.0.0
[INFO] Local version: 1.0.3
[INFO] Remote version: 1.0.5
[INFO] Update required.
[INFO] Fetching manifest...
[██████████████████████████████] 100% | 142/142 | verified
[INFO] 12 files to download (345 MB), 3 files to delete
[████████████████░░░░░░░░░░░░░░] 62% | 8/12 | 12.4 MB/s | data/level3.pak
[OK]   Download complete in 28.1s
[OK]   Local version updated to 1.0.5
[INFO] Launching game...
```

## Локальное тестирование

Серверная часть — см. `server/README.md`: для локального теста есть `local-test.sh`
(Nginx в Docker на localhost, HTTP без TLS). Для быстрой проверки достаточно любого
статического HTTP-сервера:

```bash
# 1) «сервер»: папка с version.json / manifest.json / files/...
python3 -m http.server 8765 --directory ./server

# 2) launcher.json в рабочей директории:
#    "serverUrl": "http://127.0.0.1:8765", "gameFolder": "./game"

# 3) запуск (сначала --no-launch, чтобы не стартовать игру):
dotnet run --project src/GameUpdater.Launcher -- --config ./launcher.json --no-launch
```

Сценарии: первый запуск → полное скачивание; повторный без изменений → «версии совпадают»;
изменение одного файла + новая версия через Exporter → скачивается только он; удаление файла из
сборки → лишний файл удалён локально (кроме `excludeFromDelete`).

Юнит-тесты дифа (`UpdateChecker`) — в `GameUpdater.Tests`:

```bash
dotnet run --project src/GameUpdater.Tests
```
