# AI_README.md — Инструкция для ИИ-агентов (AI Agent Context & Runbook)

> **Назначение документа:** Полный контекст проекта, обновленная архитектура максимальной защиты от взлома (Anti-Crack Architecture), учетные данные для автоматизации, пайплайн обфускации и регламент работы для нейросетевых агентов (AI / Antigravity / Cursor / Claude), продолжающих поддержку и развитие системы лицензирования WinCleaner.

---

## 1. Обзор проекта и архитектура

**WinCleaner (iOS 18 Glass Edition)** — десктопное приложение для Windows на C# (.NET Framework 4.0 / C# 5.0) для глубокой очистки системы, защиты приватности, безвозвратного уничтожения файлов (Shredder), блокировки отладчиков и сетевой изоляции (NetLockdown).
Интерфейс выполнен в современном стиле Glass UI с 60 FPS плавными анимациями, без использования тяжелых сторонних фреймворков и сторонних NuGet-библиотек.

В проект внедрена неломаемая криптографическая система лицензирования с аппаратной привязкой (HWID), асимметричной цифровой подписью Ed25519, облачным REST API на FastAPI, базой данных PostgreSQL, Telegram-ботом с защитными алертами и полным пайплайном обфускации ConfuserEx.

### Схема взаимодействия
```
[ WinCleaner.exe (C# WinForms Glass UI) ]
       │
       ├─ HWID (Volume Serial + MachineGuid + CPU Hash)
       ├─ Offline Token: %LOCALAPPDATA%\WinCleaner\license.dat (Windows DPAPI)
       ├─ Pure RFC 8032 Ed25519 C# Signature Verification (BigInteger)
       ├─ LicenseGuard: AssertOperational() в каждой функции очистки
       ├─ AntiDebug: Watchdog проверки отладчиков (dnSpy, x64dbg, CE) -> Kill()
       ├─ ConfuserEx: Anti-Tamper, Constants Encrypt, Ctrl Flow, Anti-Dump, Rename
       │
       ▼ HTTPS REST API (TLS 1.2 / TLS 1.3)
[ Render Cloud Service (FastAPI + Uvicorn + Python 3.12) ]
       │
       ├─ REST Endpoints (/api/v1/activate, validate, deactivate, admin/*, health)
       ├─ Rate-Limiting: IP Brute-Force защита (30 req/min)
       ├─ Ed25519 Private Key Signing: {license_key}|{hwid}|{expires_at}
       ├─ Real-Time Telegram Admin Security Alerts (активации, брутфорс, лимиты)
       ├─ Aiogram 3 Telegram Bot (polling, handle_signals=False, панель администратора)
       │
       ▼ AsyncPG Connection Pool
[ Render Managed PostgreSQL 16 (dpg-dbakndnbtjjs73fi4d50-a) ]
       ├─ licenses (key, user_id, days, expires_at, max_devices, status, notes)
       ├─ devices (hwid, device_name, is_active, first_seen, last_seen)
       ├─ activation_logs (license_id, hwid, action, ip, success, details, created_at)
       └─ users (telegram_id, username, first_name, created_at)
```

---

## 2. Учетные данные и секреты автоматизации

> **ВАЖНО ДЛЯ АГЕНТА:** Все учетные данные проверены и функционируют в проде.

| Параметр | Значение | Описание / Назначение |
| :--- | :--- | :--- |
| **GitHub Repo** | `https://github.com/romaleks098/wincleaner-licensing.git` | Репозиторий бэкенда (`server/`). Ветка `main`. |
| **Render API Key** | `rnd_C35MZryLOo6AOQkgQuoOm5iI821P` | Ключ доступа к Render REST API (`tea-d9njid3m8hqs73eg3ta0`). |
| **Render Service ID** | `srv-dbakvpdg1s2s73cgrvfg` | Web Service `wincleaner-license` (Region: Oregon, Python 3.12). |
| **Render Public URL** | `https://wincleaner-license.onrender.com` | Базовый URL REST API. |
| **Render Postgres ID** | `dpg-dbakndnbtjjs73fi4d50-a` | PostgreSQL 16 база данных `wincleaner-db`. |
| **Postgres Internal URL** | `postgresql://wincleaner:OjSDoXLIgwCubOyHAK4zq3cQa2iCNXXn@dpg-dbakndnbtjjs73fi4d50-a/licensing_txs1` | URL внутри сети Render. |
| **Postgres External URL** | `postgresql://wincleaner:OjSDoXLIgwCubOyHAK4zq3cQa2iCNXXn@dpg-dbakndnbtjjs73fi4d50-a.oregon-postgres.render.com:5432/licensing_txs1` | URL для внешних подключений. |
| **Telegram Bot** | `@WinCleanerLicense_Bot` | Бот для пользователей и администратора. |
| **Telegram Bot Token** | `8568126661:AAGNHt9hLR1r0YtRMI0uuyoG4ZO5i6peVqU` | Токен бота BotFather. |
| **Admin Telegram ID** | `1336498951` | ID владельца в Telegram (полные админские права и прием алертов). |
| **Admin Username** | `@DerzhalTomik` | Юзернейм владельца для связи в кнопках покупки и поддержки. |
| **Admin API Secret** | `wincleaner-super-secret-admin-2026` | Передается в заголовке `X-Admin-Token` для REST-запросов. |
| **Ed25519 Public Key (Hex)** | `acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457` | Вшит в клиент `Licensing.cs` и конфиг сервера. |
| **Ed25519 Private Key (Hex)** | `adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e` | Хранится ТОЛЬКО в env-переменных Render. |

---

## 3. Многоуровневая архитектура защиты от взлома (Anti-Crack)

Программа защищена по принципу глубокоэшелонированной обороны (Defense-in-Depth):

### 1. Асимметричная криптография Ed25519 (RFC 8032)
- Сервер подписывает строку `{license_key}|{hwid}|{expires_at}` секретным приватным ключом.
- Клиент в `Licensing.cs` проверяет подпись чистой C# реализацией на `BigInteger` (модульная арифметика $2^{255}-19$, восстановление координаты $X$, проверка $8 \cdot R + 8 \cdot S \cdot B$).
- Подделать подпись математически невозможно без приватного ключа сервера.

### 2. Аппаратная привязка HWID и DPAPI
- HWID генерируется из Volume Serial системного диска + MachineGuid из реестра + CPU identifier, хэшированных через SHA-256.
- Локальный токен хранится в `%LOCALAPPDATA%\WinCleaner\license.dat` и зашифрован через Windows DPAPI (`DataProtectionScope.CurrentUser`). Файл невозможно скопировать на другой ПК или под другую учетную запись.

### 3. Криптографическая привязка функций (`LicenseGuard`)
- При валидной подписи клиент вычисляет криптографический сессионный токен:
  `SessionToken = SHA256(SignatureBase64 + HWID + Salt)`.
- Вызов `LicenseGuard.AssertOperational()` внедрен в **каждую** ключевую функцию:
  - `Cleaner.CleanClipboard()`
  - `Cleaner.WithExplorerStopped()` (`CleanAll`, `CleanExplorer`, `CleanShellBags`)
  - `Shredder.Shred()`
  - `AppBlocker.ToggleBlock()`
  - `NetLockdown.Toggle()`
  - `MainForm` (конструктор формы)
- **Результат:** Даже если крякер попытается пропатчить `Program.Main` в dnSpy (изменить `brfalse` на `brtrue`), при нажатии любой кнопки очистки программа проверяет валидность криптографической подписи и немедленно завершает процесс (`Process.Kill()`).

### 4. Динамический Anti-Debug & Anti-Analysis (`AntiDebug`)
- Проверка наличия управляемого отладчика (`Debugger.IsAttached`, `Debugger.IsLogging`).
- Вызов нативных Win32 API: `CheckRemoteDebuggerPresent` и `IsDebuggerPresent`.
- Автоматический скан запущенных процессов реверс-инжиниринга:
  `dnSpy`, `de4dot`, `ILSpy`, `x64dbg`, `x32dbg`, `IDA`, `CheatEngine`, `Scylla`, `Charles`, `Fiddler`, `HTTPDebugger`, `ProcessHacker`, `Wireshark`.
- Фоновый сторожевой поток (`SecWatchdog`) каждые 3–5 секунд в случайные моменты времени проверяет систему и мгновенно уничтожает процесс при попытке исследования.

### 5. Защита от отката системного времени (Anti-Clock Rollback)
- При сохранении лицензии фиксируется текущее UTC-время.
- Если пользователь переводит системные часы назад для продления триала, `LicenseManager` определяет аномалию и отзывает локальный доступ.

### 6. Обфускация ConfuserEx (8 степеней защиты в `build.bat`)
Встроенный пайплайн обфускации (`tools\confuser\Confuser.CLI.exe` + `obfuscate.crproj`):
1. `<protection id="anti tamper" />` — шифрует тела методов в PE-файле и распаковывает в памяти. При модификации любого байта файла контрольная сумма ломается и программа падает.
2. `<protection id="constants" />` — шифрует строковые литералы (URL, пути, ключи) с помощью динамических дешифраторов.
3. `<protection id="ctrl flow" />` — уплощает граф потока управления методов (opaque predicates), делая декомпиляцию в dnSpy нечитаемой кашей.
4. `<protection id="anti dump" />` — стирает PE-заголовки из оперативной памяти для защиты от MegaDumper/Scylla.
5. `<protection id="anti debug" />` — инжектирует runtime-потоки для отстрела отладчиков.
6. `<protection id="invalid metadata" />` — встраивает поврежденные таблицы метаданных, ломая ILDasm и старые декомпиляторы.
7. `<protection id="ref proxy" />` — скрывает вызовы внешних API за динамическими делегатами.
8. `<protection id="rename" />` — переименовывает все типы, поля и методы в непечатаемые символы Unicode.

---

## 4. Telegram-бот и безопасность сервера

### Мгновенные защитные уведомления администратора
Сервер отправляет алерты в Telegram администратору (`ADMIN_IDS`):
- `🛡️ Успешная активация`: Ключ, имя ПК, HWID, IP-адрес, срок действия, счетчик устройств.
- `🚨 Превышен лимит устройств`: Попытка использовать ключ на лишнем компьютере.
- `⚠️ Попытка ввода неверного ключа`: Логирование подозрительных попыток.
- `🚫 Попытка активации отозванного ключа`.
- `⏰ Попытка активации просроченного ключа`.
- `⛔ Превышен Rate-Limit`: Предотвращение брутфорса ключей.

### Команды администратора в боте
- `/genkey <дни> [tg_id] [макс_устройств]` — Выпуск нового лицензионного ключа.
- `/info <KEY>` — Детальная информация: статус, срок, владелец, список привязанных ПК (имя, HWID, дата входа).
- `/unbind <KEY>` — Сброс привязки всех устройств для ключа (например, при смене материнской платы клиентом).
- `/devices <KEY>` — Список активных устройств ключа.
- `/extend <KEY> <дни>` — Продление существующей лицензии.
- `/revoke <KEY>` — Мгновенный отзыв лицензии и отключение устройств.
- `/logs` — Просмотр последних 10 событий безопасности (IP, статус, действие).
- `/stats` — Общая статистика пользователей, лицензий и устройств.
- `/admin` или `/help` — Справка по всем командам администратора.

### Пользовательское меню бота
- `🔑 Моя лицензия` — Просмотр активных ключей и срока действия.
- `💳 Купить лицензию` — Тарифы и прямая кнопка связи с админом.
- `💻 Мои устройства` — Список привязанных ПК пользователя.
- `🔄 Перенести лицензию` — Отвязка устройств для переноса ключа на новый ПК.
- `⚡ Как активировать` — Пошаговая инструкция.
- `💬 Поддержка` — Прямой контакт владельца.

---

## 5. Структура файлов проекта

```
c:\Users\romal\Downloads\Новая папка\
├── AI_README.md               # Данная инструкция для нейросетевых агентов
├── CHECKLIST.md               # Чеклист запуска и аудита
├── DEPLOY.md                  # Документация деплоя
├── build.bat                  # Главный скрипт сборки и обфускации WinCleaner.exe
├── Licensing.cs               # Модуль защиты (Ed25519, HWID, AntiDebug, LicenseGuard, UI)
├── Program.cs                 # Основной код WinCleaner (iOS 18 Glass UI)
├── WinCleaner.exe             # Готовый релизный скомпилированный и обфусцированный бинарник
├── app.ico                    # Иконка приложения
├── app.manifest               # UAC манифест (requireAdministrator, Per-Monitor DPI)
├── obfuscate.crproj           # Проект ConfuserEx с 8 уровнями защиты
├── tools/
│   ├── confuser/              # ConfuserEx v1.6.0 CLI
│   │   ├── Confuser.CLI.exe   # Консольный обфускатор
│   │   ├── Confuser.Core.dll
│   │   └── ...
│   └── patch_program.py       # Вспомогательный скрипт связывания Program.cs и Licensing.cs
├── Новая папка (2)/           # Синхронизированная копия нового исходного кода и сборщика
└── server/                    # Корень Git-репозитория бэкенда (Render)
    ├── .git/                  # Origin -> https://github.com/romaleks098/wincleaner-licensing.git
    ├── .env                   # Локальные переменные окружения
    ├── main.py                # Точка входа: lifespan FastAPI + aiogram polling
    ├── requirements.txt       # Зависимости Python (включая greenlet)
    ├── client/                # Резервная копия клиентских исходников в репозитории
    └── app/
        ├── api.py             # FastAPI роутер (/activate, /validate, /admin/*) + алерты
        ├── bot.py             # aiogram 3 роутер, админ-команды (/info, /unbind, /logs и др.)
        ├── config.py          # Pydantic v2 Settings (BaseSettings)
        ├── crypto.py          # Ed25519 подпись и генератор ключей WINC-XXXX-XXXX-XXXX
        ├── database.py        # SQLAlchemy AsyncEngine + AsyncSession
        ├── models.py          # Модели User, License, Device, ActivationLog
        └── schemas.py         # Pydantic-схемы
```

---

## 6. Регламент и команды для агента

### Сборка и обфускация Windows-клиента
Сборка выполняется запуском одного скрипта:
```powershell
cmd.exe /c "build.bat"
```
Что делает скрипт:
1. Автоматически находит Roslyn компилятор `csc.exe` (VS 2022 / VS 18 / .NET Framework 4.0).
2. Компилирует `Program.cs` и `Licensing.cs` с иконкой и манифестом в `WinCleaner_raw.exe`.
3. Запускает `tools\confuser\Confuser.CLI.exe` с конфигурацией `obfuscate.crproj`.
4. Помещает финальный неломаемый бинарник в `WinCleaner.exe`.

### Деплой изменений бэкенда на Render
Любые изменения в папке `server/` автоматически деплоятся на Render через Git:
```powershell
cd 'c:\Users\romal\Downloads\Новая папка\server'
git add .
git commit -m "Your description"
git push origin main
```
Render отслеживает коммиты в ветке `main` и автоматически пересобирает контейнер за 1.5–2 минуты.

### Проверка работоспособности сервиса
```powershell
(Invoke-RestMethod -Uri "https://wincleaner-license.onrender.com/api/v1/health") | ConvertTo-Json
```
Ожидаемый ответ:
```json
{
  "status": "ok",
  "service": "wincleaner-licensing",
  "server_time": "...",
  "public_key": "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457"
}
```

---

## 7. Критические правила для ИИ-агентов (Do's and Don'ts)

1. **Не добавлять лишний функционал в GUI:**
   Пользователь ценит минималистичный и чистый интерфейс iOS 18 Glass. Не добавляйте сторонние контролы, вкладки или карточки, если об этом явно не попросили.
2. **Никогда не убирать `LicenseGuard.AssertOperational()`:**
   Каждое действие очистки в `Cleaner`, `Shredder`, `AppBlocker`, `NetLockdown` обязано вызывать `LicenseGuard.AssertOperational()`. Это основа защиты от патчинга IL-кода.
3. **Не компилировать без обфускации:**
   Финальный файл `WinCleaner.exe` для пользователя ВСЕГДА должен проходить стадию `Confuser.CLI.exe`. Простой бинарник C# декомпилируется в dnSpy за секунды.
4. **aiogram polling внутри uvicorn:**
   В `main.py` всегда вызывать `dp.start_polling(..., handle_signals=False)`.
5. **Всегда сохранять `greenlet` в `requirements.txt`:**
   SQLAlchemy 2.x с `asyncpg` требует пакет `greenlet`, иначе сервер падает при старте.
