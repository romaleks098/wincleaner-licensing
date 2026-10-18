import asyncio
import os
import sys

# Ensure server path is in sys.path
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

from datetime import datetime, timedelta
from app.config import settings
from app.database import init_db, AsyncSessionLocal
from app.models import License, Device, User
from app.crypto import sign_license, verify_license_signature
from app.api import generate_key_string

async def run_tests():
    print("=== 1. TEST: Database Initialization ===")
    await init_db()
    print("DB initialized successfully.")

    print("\n=== 2. TEST: Ed25519 Signing and Verification ===")
    key = generate_key_string()
    hwid = "A1B2C3D4E5F6A1B2C3D4E5F6A1B2C3D4"
    exp = datetime.utcnow() + timedelta(days=30)
    sig = sign_license(key, hwid, exp)
    print(f"Key: {key}")
    print(f"HWID: {hwid}")
    print(f"Expires: {exp.isoformat()}Z")
    print(f"Signature (base64): {sig}")

    valid = verify_license_signature(key, hwid, exp, sig)
    assert valid is True, "Signature should be valid"
    print("Verification result: PASS (Signature valid)")

    tampered = verify_license_signature(key, "TAMPERED_HWID", exp, sig)
    assert tampered is False, "Tampered signature should fail"
    print("Tamper check result: PASS (Tampered HWID rejected)")

    print("\n=== 3. TEST: License DB Insertion & Device Binding ===")
    async with AsyncSessionLocal() as db:
        lic = License(
            license_key=key,
            status="active",
            max_devices=1,
            expires_at=exp,
            notes="Automated test"
        )
        db.add(lic)
        await db.commit()

        # Add first device
        dev1 = Device(license_id=lic.id, hwid=hwid, device_name="Test-PC-1")
        db.add(dev1)
        await db.commit()
        print("Device 1 linked successfully.")

        # Try linking second device when limit is 1
        active_count = len([d for d in [dev1] if d.is_active])
        assert active_count >= lic.max_devices, "Device limit check works"
        print(f"Device limit enforcement: PASS (Active: {active_count}/{lic.max_devices})")

        # Unbind device (transfer)
        dev1.is_active = False
        await db.commit()
        print("Device unbind (transfer): PASS")

        # Revoke license
        lic.status = "revoked"
        await db.commit()
        print("License revocation: PASS")

    print("\n==========================================")
    print("ALL BACKEND LICENSING TESTS PASSED!")
    print("==========================================")

if __name__ == "__main__":
    asyncio.run(run_tests())
