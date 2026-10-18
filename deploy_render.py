import os
import sys
import json
import urllib.request
import urllib.error

RENDER_API_URL = "https://api.render.com/v1"

def deploy(api_key: str, repo_url: str):
    headers = {
        "Authorization": f"Bearer {api_key.strip()}",
        "Accept": "application/json",
        "Content-Type": "application/json"
    }

    # 1. Verify API Key / Get Owner ID
    print("[1/4] Проверка Render API ключа...")
    req = urllib.request.Request(f"{RENDER_API_URL}/owners", headers=headers)
    try:
        with urllib.request.urlopen(req) as resp:
            owners = json.loads(resp.read().decode())
            if not owners:
                print("ОШИБКА: Не найден владелец аккаунта Render.")
                return
            owner_id = owners[0]["owner"]["id"]
            owner_email = owners[0]["owner"].get("email", "")
            print(f"Владелец: {owner_email} (ID: {owner_id})")
    except urllib.error.HTTPError as e:
        print(f"ОШИБКА авторизации Render: {e.code} {e.read().decode()}")
        return

    # 2. Check or Create PostgreSQL Database
    print("\n[2/4] Создание базы данных PostgreSQL...")
    db_payload = {
        "name": "wincleaner-db",
        "ownerId": owner_id,
        "plan": "free",
        "region": "frankfurt",
        "databaseName": "licensing",
        "databaseUser": "wincleaner"
    }
    req_db = urllib.request.Request(
        f"{RENDER_API_URL}/postgres",
        data=json.dumps(db_payload).encode(),
        headers=headers,
        method="POST"
    )
    db_conn_str = None
    try:
        with urllib.request.urlopen(req_db) as resp:
            db_data = json.loads(resp.read().decode())
            print(f"База данных создана: {db_data.get('id')}")
    except urllib.error.HTTPError as e:
        print(f"Инфо базы: {e.code} (возможно, уже существует)")

    # 3. Create Web Service
    print("\n[3/4] Создание Web Service из репозитория...")
    svc_payload = {
        "type": "web_service",
        "name": "wincleaner-license-service",
        "ownerId": owner_id,
        "repo": repo_url.strip(),
        "branch": "main",
        "rootDir": "server",
        "env": "docker",
        "plan": "free",
        "region": "frankfurt",
        "envVars": [
            {"key": "BOT_TOKEN", "value": "8568126661:AAGNHt9hLR1r0YtRMI0uuyoG4ZO5i6peVqU"},
            {"key": "BOT_USERNAME", "value": "WinCleanerLicense_Bot"},
            {"key": "ADMIN_IDS", "value": "1336498951"},
            {"key": "ED25519_PRIVATE_KEY_HEX", "value": "adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e"},
            {"key": "ED25519_PUBLIC_KEY_HEX", "value": "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457"},
            {"key": "ADMIN_SECRET", "value": "wincleaner-super-secret-admin-2026"}
        ]
    }
    req_svc = urllib.request.Request(
        f"{RENDER_API_URL}/services",
        data=json.dumps(svc_payload).encode(),
        headers=headers,
        method="POST"
    )
    try:
        with urllib.request.urlopen(req_svc) as resp:
            svc_data = json.loads(resp.read().decode())
            service_url = svc_data["service"]["serviceDetails"]["url"]
            print(f"\n[УСПЕХ] Сервис создан и разворачивается!")
            print(f"Боевой URL: {service_url}")
    except urllib.error.HTTPError as e:
        print(f"Инфо сервиса: {e.code} {e.read().decode()}")

if __name__ == "__main__":
    if len(sys.argv) < 3:
        print("Использование: python deploy_render.py <RENDER_API_KEY> <GITHUB_REPO_URL>")
        sys.exit(1)
    deploy(sys.argv[1], sys.argv[2])
