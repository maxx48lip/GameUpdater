# GameUpdater

Система автообновления игры: **Exporter** (сборка → манифест → заливка по SFTP), **Launcher**
(проверка версии, докачка файлов, запуск игры), общий модуль **Shared** и серверная часть на VPS
(Nginx в Docker + OpenSSH SFTP).

- Платформа: **.NET 10 (LTS)**, консольные приложения.
- **Никаких NuGet-пакетов** — только shared framework (`System.Text.Json`,
  `System.Security.Cryptography`, `System.Diagnostics.Process`, `System.IO`).
- SFTP — через внешний клиент OpenSSH (`sftp` / `sftp.exe`), без сторонних библиотек.
- Целевые ОС: Windows 10+, Linux (x64/arm64), macOS 12+ (Intel и Apple Silicon).

## Структура репозитория

```text
RemasterLoading/
├── GameUpdater/                 # .NET-решение (клиентская часть)
│   ├── GameUpdater.sln
│   └── src/
│       ├── GameUpdater.Shared/        # Class Library, net10.0
│       │   ├── Models/                # VersionInfo, Manifest, FileEntry (System.Text.Json)
│       │   ├── HashCalculator.cs      # потоковый MD5
│       │   ├── PathNormalizer.cs      # unix-пути, расширение ~
│       │   ├── AnsiConsole.cs         # цветной лог + прогресс-бар (ANSI)
│       │   ├── ByteFormat.cs          # "12.3 MB" и т.п.
│       │   └── SftpClientWrapper.cs   # обёртка над внешним процессом sftp
│       ├── GameUpdater.Exporter/      # Console, net10.0 (бинарь: exporter)
│       │   ├── Program.cs             # точка входа, ручной разбор args[]
│       │   ├── CliOptions.cs          # парсинг CLI + usage
│       │   ├── ExporterConfig.cs      # модель exporter.json
│       │   ├── ManifestBuilder.cs     # обход папки, ignorePatterns, MD5, сортировка
│       │   ├── SftpUploader.cs        # заливка: files → manifest.json → version.json
│       │   └── exporter.json          # пример конфигурации
│       ├── GameUpdater.Launcher/      # Console, net10.0 (бинарь: launcher)
│       │   ├── Program.cs             # точка входа: конфиг → версия → манифест → диф → скачивание → запуск
│       │   ├── CliOptions.cs          # парсинг CLI + usage
│       │   ├── LauncherConfig.cs      # модель launcher.json
│       │   ├── UpdateChecker.cs       # версии, диф по манифесту (размер/MD5), excludeFromDelete
│       │   ├── FileDownloader.cs      # 3 потока, .tmp + атомарный Move, ретраи, проверка хеша
│       │   ├── GameLauncher.cs        # запуск игры: Windows / Linux (chmod +x) / macOS (.app)
│       │   └── launcher.json          # пример конфигурации
│       └── GameUpdater.Tests/         # Console-тесты без NuGet (свои Assert)
├── server/                      # Серверная часть: VPS (Debian 12, Docker, Nginx, SFTP) — см. server/README.md
│   ├── setup-vps.sh             # идемпотентный скрипт первичной настройки VPS
│   ├── nginx.conf               # эталонная копия конфига Nginx (fileserver)
│   ├── sshd_config_snippet      # эталонная копия фрагмента sshd (SFTP chroot для exporter)
│   └── local-test.sh            # локальный тест: Nginx в Docker на localhost + тестовая сборка
└── .github/workflows/release.yml  # CI: сборка, тесты, self-contained publish, GitHub Release
```

## Сборка и запуск тестов

```bash
dotnet build GameUpdater/GameUpdater.sln          # сборка всего решения
dotnet run --project GameUpdater/src/GameUpdater.Tests   # юнит-тесты ManifestBuilder/Shared
```

## Серверная часть

VPS на Debian 12: Nginx в Docker (`fileserver`) отдаёт статику по HTTPS, OpenSSH SFTP (chroot)
принимает заливку от Exporter. Развёртывание — `server/setup-vps.sh`, локальный тест без VPS —
`server/local-test.sh`. Подробности: [`server/README.md`](server/README.md).

> Для chroot-сервера из `setup-vps.sh` в `exporter.json` указывается **`remoteBasePath: "/www"`**
> (путь внутри chroot), а не `/srv/gamefiles` (путь на диске) — см. `server/README.md`.

## CI / Release (GitHub Actions)

`.github/workflows/release.yml` — на каждый push в `main`:

1. Сборка решения (`Release`) и прогон тестов.
2. Self-contained publish Exporter и Launcher под `win-x64` и `osx-arm64`.
3. Упаковка в zip + `SHA256SUMS.txt`.
4. Создание GitHub **prerelease** с тегом `v<ГГГГ.ММ.ДД>-<sha7>` (артефакты:
   `gameup-exporter-<rid>.zip`, `gameup-launcher-<rid>.zip`).

## Использование Exporter

```bash
exporter --version 1.0.5 --path ./build --config ./exporter.json
```

| Аргумент | Обязательный | Описание |
|---|---|---|
| `--version <ver>` | да | Строка версии, например `1.0.5` |
| `--path <dir>` | нет* | Путь к папке со сборкой (или `"buildFolder"` в конфиге; CLI имеет приоритет) |
| `--config <file>` | нет | Конфиг (по умолчанию `./exporter.json`) |
| `--update-wiki` | нет | Обновить wiki из `"wikiFolder"` (дельта-заливка по `wiki_manifest.json`). Без флага wiki не трогается вовсе |
| `--dry-run` | нет | Сгенерировать манифест локально, не заливать |
| `--skip-upload` | нет | Пересчитать манифест локально, не заливать |
| `--verbose` | нет | Подробный лог (каждый файл при хешировании) |
| `--no-color` | нет | Отключить ANSI-цвета |

\*Обязателен, если в конфиге не задан `"buildFolder"`.

Коды выхода: `0` — успех, `1` — ошибка выполнения, `2` — ошибка аргументов, `130` — отмена (Ctrl+C).

### Пример вывода

```text
[INFO]  Exporter v1.0.0
[INFO]  Version: 1.0.5
[INFO]  Scanning ./build...
[██████████████████████████████] 100% | 142/142 | scanning
[INFO]  Found 142 files (2.3 GB)
[INFO]  Manifest generated: 142 entries
[INFO]  Uploading to files.example.com...
[████████████░░░░░░░░░░░░░░░░░░] 62% | 88/142 | 12.4 MB/s | files/data/level3.pak
[OK]    Upload complete in 45.2s
[OK]    version.json updated to 1.0.5
```

## Конфигурация (exporter.json)

```json
{
  "sftpHost": "files.example.com",
  "sftpPort": 22,
  "sftpUser": "exporter",
  "sftpKeyPath": "~/.ssh/id_ed25519",
  "remoteBasePath": "/srv/gamefiles",
  "buildFolder": "/path/to/build",
  "ignorePatterns": ["*.log", "*.pdb", "local_version.json", ".DS_Store"],
  "wikiFolder": "/path/to/Wiki",
  "wikiIgnorePatterns": [".git", "md", "scripts/__pycache__"],
  "minLauncherVersion": "1.0.0",
  "uploadMode": "perFile",
  "maxRetries": 3
}
```

| Поле | Описание |
|---|---|
| `sftpHost`, `sftpPort`, `sftpUser` | Адрес SFTP-сервера (OpenSSH, порт по умолчанию 22) |
| `sftpKeyPath` | Путь к приватному ключу (`~` раскрывается). **Рекомендуется.** |
| `remoteBasePath` | Базовая папка на сервере (см. структуру ниже; для chroot-сервера — `/www`) |
| `buildFolder` | Необязательно. Путь к папке со сборкой (используется, если не передан `--path`; CLI имеет приоритет) |
| `ignorePatterns` | Глоб-паттерны (`*`, `?`) для файлов и каталогов; применяются к имени файла и относительному пути, без учёта регистра |
| `wikiFolder` | Необязательно. Путь к папке статической wiki: её содержимое заливается в `<remoteBasePath>/wiki/` (доступно по `https://<host>/wiki/`) **вне** манифеста — Launcher её не видит и не скачивает. Обновляется только с флагом `--update-wiki`, дельтой (см. ниже) |
| `wikiIgnorePatterns` | Глоб-паттерны для сканирования wiki (в дополнение к встроенному мусору ОС); например `.git`, папки с исходниками |
| `minLauncherVersion` | Попадает в `version.json` (по умолчанию `1.0.0`) |
| `uploadMode` | `perFile` (по умолчанию) или `batch` — см. ниже |
| `maxRetries` | Попыток на файл при сбое загрузки (по умолчанию 3, backoff 2s/4s/8s…) |

Всегда игнорируются дополнительно: `.DS_Store`, `Thumbs.db`, `desktop.ini`, а также
`manifest.json` и `version.json` в корне сборки (это артефакты самого экспортера).

### Аутентификация

- **По ключу** (рекомендуется): укажите `sftpKeyPath`. Ключ должен быть без фразы пароля.
- **По паролю**: переменная окружения `SFTP_PASSWORD` (пароль нигде не хранится).
  Требует установленный `sshpass`, т.к. OpenSSH sftp не умеет принимать пароль
  в неинтерактивном режиме:

```bash
export SFTP_PASSWORD='...'   # и уберите sftpKeyPath из конфига
```

### Структура на сервере

```text
/srv/gamefiles/
├── version.json          ← перезаписывается ПОСЛЕДНИМ
├── manifest.json         ← список файлов с MD5 (игровые файлы)
├── wiki_manifest.json    ← список файлов wiki: path + size + MD5 (дельта-заливка)
├── files/                ← игровые файлы (относительные пути из сборки)
│   ├── client.exe
│   └── data/level1.pak
└── wiki/                 ← статическая wiki (если задан "wikiFolder"), вне манифеста игры
    ├── index.html        → https://<host>/wiki/
    └── pages/*.html      → перекрёстные ссылки — относительные, работают как есть
```

Порядок заливки критичен и соблюдается всегда: **сначала (если задан `--update-wiki`) wiki в
`wiki/`, затем все файлы в `files/`, затем `manifest.json`, последним — `version.json`**.
Launcher увидит новую версию, только когда манифест и все файлы уже на месте.
Каталогная структура создаётся автоматически (mkdir-сессия до загрузки).

Wiki не попадает ни в `manifest.json`, ни в `version.json`: Launcher её не видит,
она просто отдаётся Nginx'ом по адресу `/wiki/` (ссылки внутри wiki относительные —
перекрёстные переходы работают без изменений). Обновляется она **дельтой**: Exporter
считывает удалённый `wiki_manifest.json`, сравнивает MD5 и грузит только изменившиеся/новые
файлы, удаляет устаревшие (их больше нет локально) и лишь затем публикует новый
`wiki_manifest.json`. Если заливка прервётся до публикации манифеста — при следующем запуске
удалённые файлы просто перезагрузятся (старый манифест их ещё помнит).

### Режимы загрузки

- **`perFile`** (по умолчанию): отдельный процесс sftp на каждый файл (`sftp -b`).
  Медленнее, но каждая загрузка независимо ретраится, а прогресс точен.
- **`batch`**: один сессионный процесс для всех файлов. Быстрее; при обрыве
  (sftp `-b` прерывается на первой ошибке) оставшиеся файлы автоматически
  догружаются пофайлово с ретраями.

## Использование Launcher

```bash
launcher [--config <file>] [options]
```

| Аргумент | Обязательный | Описание |
|---|---|---|
| `--config <file>` | нет | Конфиг (по умолчанию `launcher.json` рядом с exe) |
| `--no-launch` | нет | Только обновление, без запуска игры |
| `--no-color` | нет | Отключить ANSI-цвета |
| `-h`, `--help` | нет | Справка |

Коды выхода: `0` — успех, `1` — ошибка выполнения, `2` — ошибка аргументов, `130` — отмена (Ctrl+C).

### Конфигурация (launcher.json)

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
| `serverUrl` | Базовый URL сервера (http/https): `{serverUrl}/version.json`, `/manifest.json`, `/files/<путь>` |
| `gameFolder` | Папка с игрой (относительный путь — от CWD, `~` раскрывается); создаётся автоматически |
| `executable` | Исполняемый файл игры относительно `gameFolder`; на macOS может быть `.app`-бандлом |
| `excludeFromDelete` | Что не удалять при очистке: точное имя файла, имя каталога (всё дерево) или глобы (`*`, `?`, `**`). `local_version.json` исключается всегда |

Алгоритм: версия → совпадают? запуск игры : манифест → диф (нет файла / размер / MD5) →
параллельное скачивание 3 потоками во `.tmp` с проверкой хеша и атомарным Move (ретраи до 3 раз)
→ удаление лишних файлов → запись `local_version.json` → запуск игры.
Ctrl+C — отмена, удаление `.tmp`, код выхода 130. Подробности: `GameUpdater/src/GameUpdater.Launcher/README.md`.

## Форматы манифестов

`manifest.json`:

```json
{
  "version": "1.0.5",
  "algorithm": "MD5",
  "files": [
    { "path": "client.exe", "hash": "d41d8cd98f00b204e9800998ecf8427e", "size": 12582912 },
    { "path": "data/level1.pak", "hash": "a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6", "size": 52428800 }
  ]
}
```

Пути — относительные, с прямыми слэшами (`/`), отсортированы по пути. MD5 считается
потоково (файл целиком в память не загружается).

`version.json`:

```json
{
  "version": "1.0.5",
  "buildDate": "2026-10-13T12:00:00Z",
  "minLauncherVersion": "1.0.0"
}
```

## Консольный вывод (AnsiConsole)

- Цветной лог через ANSI-escape: `INFO` — голубой, `WARN` — жёлтый, `ERROR` — красный, `OK` — зелёный.
- Прогресс-бар — одна строка через `\r`, не чаще 10 обновлений/с; потокобезопасен (общий lock).
- Windows: виртуальный терминал включается P/Invoke `SetConsoleMode` + UTF-8.
- **Fallback без TTY**: при перенаправлении вывода (`Console.IsOutputRedirected`)
  прогресс-бар и цвета отключаются, остаётся только лог — удобно для CI-логов.

## Кроссплатформенность

| Пункт | Решение |
|---|---|
| sftp | Linux/macOS — `sftp`, Windows — `sftp.exe` (OpenSSH в Win 10 1809+); поиск в PATH + проверка `-V` |
| Пути | `Path.Combine` / `Path.GetRelativePath`, нормализация `.Replace('\\', '/')` |
| Домашняя папка | `Environment.SpecialFolder.UserProfile` для `~` |
| Ключи | `~/.ssh/id_ed25519` на всех трёх ОС |

Проверено на macOS (Intel/ARM, .NET 10). Windows/Linux — те же кодовые пути;
отличия изолированы в `AnsiConsole` (P/Invoke) и поиске `sftp.exe`.

## Тестирование

```bash
dotnet run --project GameUpdater/src/GameUpdater.Tests
```

Тесты без NuGet (собственные Assert-функции): обход папки, ignorePatterns
(файлы и каталоги), сортировка путей, unix-формат путей в манифесте, MD5 по
известным векторам, форма JSON (`version`/`algorithm`/`files`, `path`/`hash`/`size`),
диф Launcher (`UpdateChecker`: нет файла / размер отличается / хеш не совпал, список на
удаление с учётом `excludeFromDelete`).

Ручная проверка: `--dry-run` и `--skip-upload` на локальной папке; сверка хешей
с `md5sum` (Linux) или `md5 -r` (macOS). Серверную часть можно проверить без VPS через
`server/local-test.sh` — см. `server/README.md`.
