import base64
from datetime import datetime
from cryptography.hazmat.primitives.asymmetric import ed25519
from cryptography.hazmat.primitives import serialization
from app.config import settings

def get_private_key() -> ed25519.Ed25519PrivateKey:
    raw = bytes.fromhex(settings.ED25519_PRIVATE_KEY_HEX.strip())
    return ed25519.Ed25519PrivateKey.from_private_bytes(raw)

def get_public_key() -> ed25519.Ed25519PublicKey:
    raw = bytes.fromhex(settings.ED25519_PUBLIC_KEY_HEX.strip())
    return ed25519.Ed25519PublicKey.from_public_bytes(raw)

def build_canonical_payload(license_key: str, hwid: str, expires_at: datetime) -> bytes:
    """
    Format must match C# LicenseInfo.ToCanonicalString:
    {license_key}|{hwid}|{expires_at_iso_utc}
    """
    exp_str = expires_at.strftime("%Y-%m-%dT%H:%M:%SZ")
    payload = f"{license_key.strip()}|{hwid.strip().upper()}|{exp_str}"
    return payload.encode("utf-8")

def sign_license(license_key: str, hwid: str, expires_at: datetime) -> str:
    """
    Signs the canonical license payload and returns Base64 encoded Ed25519 signature.
    """
    priv = get_private_key()
    payload = build_canonical_payload(license_key, hwid, expires_at)
    sig = priv.sign(payload)
    return base64.b64encode(sig).decode("ascii")

def verify_license_signature(license_key: str, hwid: str, expires_at: datetime, signature_b64: str) -> bool:
    try:
        pub = get_public_key()
        payload = build_canonical_payload(license_key, hwid, expires_at)
        sig = base64.b64decode(signature_b64)
        pub.verify(sig, payload)
        return True
    except Exception:
        return False

def generate_key_string() -> str:
    import secrets
    alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"
    parts = ["WINC"]
    for _ in range(3):
        parts.append("".join(secrets.choice(alphabet) for _ in range(4)))
    return "-".join(parts)

