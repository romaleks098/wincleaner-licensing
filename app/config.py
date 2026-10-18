import os
from typing import List

try:
    from pydantic_settings import BaseSettings
    class SettingsBase(BaseSettings):
        class Config:
            env_file = ".env"
            extra = "allow"
except ImportError:
    class SettingsBase:
        pass

class Settings(SettingsBase):
    def __init__(self):
        self.HOST: str = os.getenv("HOST", "0.0.0.0")
        self.PORT: int = int(os.getenv("PORT", "8000"))
        self.ENVIRONMENT: str = os.getenv("ENVIRONMENT", "production")
        self.DATABASE_URL: str = os.getenv("DATABASE_URL", "sqlite+aiosqlite:///./licensing.db")
        self.BOT_TOKEN: str = os.getenv("BOT_TOKEN", "")
        self.BOT_USERNAME: str = os.getenv("BOT_USERNAME", "WinCleanerLicenseBot")
        self.ADMIN_IDS_RAW: str = os.getenv("ADMIN_IDS", "")
        self.ED25519_PRIVATE_KEY_HEX: str = os.getenv(
            "ED25519_PRIVATE_KEY_HEX",
            "adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e"
        )
        self.ED25519_PUBLIC_KEY_HEX: str = os.getenv(
            "ED25519_PUBLIC_KEY_HEX",
            "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457"
        )
        self.ADMIN_SECRET: str = os.getenv("ADMIN_SECRET", "super-secret-admin-token-for-api-calls")

    @property
    def async_database_url(self) -> str:
        url = self.DATABASE_URL
        if url.startswith("postgres://"):
            url = url.replace("postgres://", "postgresql+asyncpg://", 1)
        elif url.startswith("postgresql://") and not url.startswith("postgresql+asyncpg://"):
            url = url.replace("postgresql://", "postgresql+asyncpg://", 1)
        return url

    @property
    def admin_ids(self) -> List[int]:
        if not self.ADMIN_IDS_RAW:
            return []
        ids = []
        for x in self.ADMIN_IDS_RAW.split(","):
            s = x.strip()
            if s.isdigit():
                ids.append(int(s))
        return ids

settings = Settings()
