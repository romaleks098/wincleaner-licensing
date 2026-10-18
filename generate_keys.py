#!/usr/bin/env python3
"""
Ed25519 Keypair Generator for WinCleaner Licensing System
"""
from cryptography.hazmat.primitives.asymmetric import ed25519
from cryptography.hazmat.primitives import serialization

def generate_keypair():
    priv = ed25519.Ed25519PrivateKey.generate()
    pub = priv.public_key()

    priv_bytes = priv.private_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PrivateFormat.Raw,
        encryption_algorithm=serialization.NoEncryption()
    )
    pub_bytes = pub.public_bytes(
        encoding=serialization.Encoding.Raw,
        format=serialization.PublicFormat.Raw
    )

    print("=" * 70)
    print("WINCLEANER ED25519 KEYPAIR GENERATED")
    print("=" * 70)
    print(f"ED25519_PRIVATE_KEY_HEX={priv_bytes.hex()}")
    print(f"ED25519_PUBLIC_KEY_HEX={pub_bytes.hex()}")
    print("=" * 70)
    print("Add ED25519_PRIVATE_KEY_HEX to your Render environment secrets.")
    print("Embed ED25519_PUBLIC_KEY_HEX into C# LicenseManager.PublicKeyHex.")
    print("=" * 70)

if __name__ == "__main__":
    generate_keypair()
