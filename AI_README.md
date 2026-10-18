# AI_README.md — Инструкция для ИИ-агентов (AI Agent Context & Runbook)

> **Назначение документа:** Полный контекст проекта, архитектурные решения, учетные данные для автоматизации и регламент работы для нейросетевых агентов (AI / Antigravity / Cursor / Claude), продолжающих поддержку и развитие системы лицензирования WinCleaner.

---

## 1. Обзор проекта и архитектура

**WinCleaner** — десктопное Windows Forms приложение на C# (.NET Framework 4.0 / C# 5.0) для очистки и оптимизации системы.
В проект внедрена криптографическая система лицензирования с аппаратной привязкой (HWID), асимметричной цифровой подписью Ed25519, облачным REST API, базой данных PostgreSQL и Telegram-ботом управления.

### Схема взаимодействия
```
[ WinCleaner.exe (C# WinForms) ]
       │
       ├─ HWID (Volume Serial + MachineGuid + CPU Hash)
       ├─ Offline Token: %LOCALAPPDATA%\WinCleaner\license.dat (Windows DPAPI)
       ├─ RFC 8032 Ed25519 Pure C# Signature Verification (BigInteger, no NuGet)
       │
       ▼ HTTPS REST API
[ Render Cloud Service (FastAPI + Uvicorn + Python 3.12) ]
       │
       ├─ REST Endpoints (/api/v1/activate, validate, deactivate, admin/*, health)
       ├─ Ed25519 Private Key Signing: {license_key}|{hwid}|{expires_at}
       ├─ Aiogram 3 Telegram Bot (background polling, handle_signals=False)
       │
       ▼ AsyncPG Connection Pool
[ Render Managed PostgreSQL 16 (dpg-dbakndnbtjjs73fi4d50-a) ]
       ├─ licenses (key, days, expires_at, max_devices, status, notes)
       ├─ devices (hwid, device_name, activated_at, last_check_at)
       └─ audit_logs (action, ip, details, created_at)
```

---

## 2. Учетные данные и секреты автоматизации

> **ВАЖНО ДЛЯ АГЕНТА:** Все ключи и токены проверены и работают в проде.

| Параметр | Значение | Описание / Назначение |
| :--- | :--- | :--- |
| **GitHub Repo** | `https://github.com/romaleks098/wincleaner-licensing.git` | Репозиторий бэкенда (`server/`). Ветка `main`. |
| **Render API Key** | `rnd_C35MZryLOo6AOQkgQuoOm5iI821P` | Ключ доступа к Render REST API (`tea-d9njid3m8hqs73eg3ta0`). |
| **Render Service ID** | `srv-dbakvpdg1s2s73cgrvfg` | Web Service `wincleaner-license` (Region: Oregon, Python 3.12). |
| **Render Public URL** | `https://wincleaner-license.onrender.com` | Базовый URL REST API. |
| **Render Postgres ID** | `dpg-dbakndnbtjjs73fi4d50-a` | PostgreSQL 16 база данных `wincleaner-db`. |
| **Postgres Internal URL** | `postgresql://wincleaner:OjSDoXLIgwCubOyHAK4zq3cQa2iCNXXn@dpg-dbakndnbtjjs73fi4d50-a/licensing_txs1` | URL внутри сети Render. |
| **Postgres External URL** | `postgresql://wincleaner:OjSDoXLIgwCubOyHAK4zq3cQa2iCNXXn@dpg-dbakndnbtjjs73fi4d50-a.oregon-postgres.render.com:5432/licensing_txs1` | URL для внешних подключений/миграций. |
| **Telegram Bot** | `@WinCleanerLicense_Bot` | Бот для пользователей и администратора. |
| **Telegram Bot Token** | `8568126661:AAGNHt9hLR1r0YtRMI0uuyoG4ZO5i6peVqU` | Токен бота BotFather. |
| **Admin Telegram ID** | `1336498951` | ID владельца в Telegram (полные админские права в боте). |
| **Admin Username** | `@DerzhalTomik` | Юзернейм владельца для связи в кнопках покупки и поддержки. |
| **Admin API Secret** | `wincleaner-super-secret-admin-2026` | Передается в заголовке `X-Admin-Token` для админских REST-запросов. |
| **Ed25519 Public Key (Hex)** | `acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457` | Вшит в клиент `Licensing.cs` и конфиг сервера. |
| **Ed25519 Private Key (Hex)** | `adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e` | Хранится ТОЛЬКО в env-переменных Render. |

---

## 3. Структура файлов проекта

```
c:\Users\romal\Downloads\Новая папка\
├── AI_README.md               # Данный файл инструкций для нейронок
├── CHECKLIST.md               # Чеклист готовности и аудита
├── DEPLOY.md                  # Документация по ручному деплою
├── build.bat                  # Скрипт сборки WinCleaner.exe через csc.exe
├── Licensing.cs               # Модуль лицензирования C# (.NET 4.0, Ed25519, HWID, UI)
├── WinCleaner.cs              # Основной код приложения WinForms
├── WinCleaner.exe             # Скомпилированный бинарник
├── app.ico                    # Иконка приложения
├── app.manifest               # Манифест Windows (UAC, DPI-awareness)
└── server/                    # Корень Git-репозитория бэкенда
    ├── .git/                  # Git репозиторий origin -> github
    ├── .env                   # Локальные переменные окружения
    ├── .gitignore             # Игнор pycache, venv, sqlite
    ├── requirements.txt       # Зависимости Python
    ├── main.py                # Точка входа: lifespan FastAPI + aiogram polling
    ├── generate_keys.py       # Утилита генерации новой пары Ed25519
    ├── test_licensing.py      # Набор интеграционных тестов
    └── app/
        ├── config.py          # Pydantic v2 Settings (BaseSettings)
        ├── crypto.py          # Подпись и верификация Ed25519 через cryptography
        ├── database.py        # SQLAlchemy AsyncEngine + AsyncSession + retry logic
        ├── models.py          # Модели License, Device, AuditLog
        ├── schemas.py         # Pydantic-схемы запросов и ответов
        ├── api.py             # FastAPI роутер (/activate, /validate, /admin/*)
        └── bot.py             # aiogram 3 роутер и UI кнопки
```

---

## 4. Регламент и готовые команды для агента

### Сборка C# клиента
Компиляция строго без сторонних NuGet-библиотек:
```powershell
cmd.exe /c build.bat
```
Либо прямой вызов Roslyn компилятора:
```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\Roslyn\csc.exe" `
  /target:winexe /optimize+ /debug- `
  /out:WinCleaner.exe `
  /win32icon:app.ico `
  /win32manifest:app.manifest `
  /reference:System.dll,System.Core.dll,System.Drawing.dll,System.Windows.Forms.dll,System.Data.dll,System.Xml.dll,System.Numerics.dll,System.Security.dll `
  WinCleaner.cs Licensing.cs
```

### Деплой изменений бэкенда
1. Вносить правки в `c:\Users\romal\Downloads\Новая папка\server\`.
2. Коммитить и пушить в ветку `main`:
   ```powershell
   cd 'c:\Users\romal\Downloads\Новая папка\server'
   git add .
   git commit -m "Your description"
   git push origin main
   ```
3. Render отслеживает коммиты в `main` и запускает автодеплой за 1.5–2 минуты.

### Мониторинг статуса Render и логов через Python
```python
import urllib.request, json

API_KEY = "rnd_C35MZryLOo6AOQkgQuoOm5iI821P"
SERVICE_ID = "srv-dbakvpdg1s2s73cgrvfg"
OWNER_ID = "tea-d9njid3m8hqs73eg3ta0"

# Проверка статуса деплоя
req = urllib.request.Request(
    f"https://api.render.com/v1/services/{SERVICE_ID}/deploys?limit=1",
    headers={"Authorization": f"Bearer {API_KEY}"}
)
res = json.loads(urllib.request.urlopen(req, timeout=5).read().decode())
print("STATUS:", res[0]["deploy"]["status"])

# Чтение логов сервиса
req_logs = urllib.request.Request(
    f"https://api.render.com/v1/logs?ownerId={OWNER_ID}&resource={SERVICE_ID}&limit=20",
    headers={"Authorization": f"Bearer {API_KEY}"}
)
logs = json.loads(urllib.request.urlopen(req_logs, timeout=5).read().decode())
for line in reversed(logs.get("logs", [])):
    print(line.get("timestamp"), line.get("message"))
```

### Генерация ключа через API (REST)
```python
import urllib.request, json

req = urllib.request.Request(
    "https://wincleaner-license.onrender.com/api/v1/admin/generate",
    data=json.dumps({"days": 30, "max_devices": 2, "notes": "Generated by AI"}).encode("utf-8"),
    headers={
        "Content-Type": "application/json",
        "X-Admin-Token": "wincleaner-super-secret-admin-2026"
    }
)
res = json.loads(urllib.request.urlopen(req, timeout=5).read().decode())
print("NEW KEY:", res["license_key"])
```

### Тестирование активации ключа клиентом
```python
import urllib.request, json

req = urllib.request.Request(
    "https://wincleaner-license.onrender.com/api/v1/activate",
    data=json.dumps({
        "license_key": "WINC-XXXX-XXXX-XXXX",
        "hwid": "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
        "device_name": "TEST-RIG"
    }).encode("utf-8"),
    headers={"Content-Type": "application/json"}
)
res = json.loads(urllib.request.urlopen(req, timeout=5).read().decode())
print("ACTIVATED:", res["status"], "SIG:", res["signature"])
```

---

## 5. Грабли, критические нюансы и решения (Lessons Learned)

1. **`greenlet` в `requirements.txt`:**
   SQLAlchemy 2.x с драйвером `asyncpg` требует пакет `greenlet`. Без него сервер падает при старте с кодом 1:
   ```
   ImportError: The SQLAlchemy asyncio module requires that the Python 'greenlet' library is installed.
   ```
   В `requirements.txt` ВСЕГДА должны присутствовать `sqlalchemy[asyncio]>=2.0.35` и `greenlet>=3.0.0`.

2. **aiogram polling внутри uvicorn:**
   `dp.start_polling` по умолчанию перехватывает системные сигналы (`SIGINT`, `SIGTERM`), что приводит к конфликту с главным потоком uvicorn:
   ```python
   # ПРАВИЛЬНО:
   await dp.start_polling(bot_instance, handle_signals=False)
   ```

3. **Неблокирующий Lifespan:**
   Render Free Plan проверяет доступность порта сразу после старта контейнера. Если подключение к БД (`init_db`) или запуск бота делать синхронно в `lifespan` до `yield`, деплой может упасть по таймауту ожидания порта. В `main.py` фоновые задачи стартуют через `asyncio.create_task()`, а `lifespan` мгновенно делает `yield`.

4. **Порт Render:**
   Render передает порт через переменную `$PORT` (по умолчанию 10000). Код `main.py` считывает `int(os.environ.get("PORT", "10000"))`.

5. **Pydantic v2 `BaseSettings`:**
   В Pydantic v2 нельзя переопределять `def __init__(...)` в наследниках `BaseSettings` без явного `super().__init__(**kwargs)` — это вызывает `AttributeError: '__pydantic_extra__'`. Используется чистый декларативный стиль с `model_config = SettingsConfigDict(env_file=".env", extra="allow")`.

6. **Криптография в C# 5 / .NET 4.0:**
   Стандартный BCL .NET 4.0 не содержит Ed25519. Сторонние библиотеки TweetNaCl из-за багов переноса разрядов (32-bit limbs) падали на верификации. Проблема решена написанием чистой RFC 8032 реализации с использованием `System.Numerics.BigInteger` (модульная арифметика $2^{255}-19$, восстановление координаты $X$, проверка $8 \cdot R + 8 \cdot S \cdot B$).

7. **Локальное хранилище лицензии:**
   Токен хранится в `%LOCALAPPDATA%\WinCleaner\license.dat` и зашифрован Windows DPAPI (`DataProtectionScope.CurrentUser`). Файл невозможно скопировать на другой ПК или прочитать под другой учетной записью.

8. **TLS 1.2 в .NET Framework 4.0 runtime:**
   В .NET Framework 4.0 по умолчанию `ServicePointManager.SecurityProtocol` использует только SSL 3.0 и TLS 1.0. При обращении к современным HTTPS-серверам (Cloudflare, Render) запрос падает с ошибкой: `Не удалось создать защищенный канал SSL/TLS`. В `LicenseManager` принудительно включены протоколы TLS 1.2 (код 3072) и TLS 1.3 (код 12288):
   ```csharp
   ServicePointManager.SecurityProtocol = (SecurityProtocolType)12288 | (SecurityProtocolType)3072 | (SecurityProtocolType)768 | (SecurityProtocolType)192;
   ServicePointManager.ServerCertificateValidationCallback = delegate { return true; };
   ```
