import os
from typing import List
from pydantic_settings import BaseSettings, SettingsConfigDict

class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", extra="allow")

    HOST: str = "0.0.0.0"
    PORT: int = 8000
    ENVIRONMENT: str = "production"
    DATABASE_URL: str = "sqlite+aiosqlite:///./licensing.db"
    BOT_TOKEN: str = ""
    BOT_USERNAME: str = "WinCleanerLicense_Bot"
    ADMIN_IDS: str = ""
    ED25519_PRIVATE_KEY_HEX: str = "adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e"
    ED25519_PUBLIC_KEY_HEX: str = "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457"
    ADMIN_SECRET: str = "super-secret-admin-token-for-api-calls"

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
        if not self.ADMIN_IDS:
            return []
        ids = []
        for x in self.ADMIN_IDS.split(","):
            s = x.strip()
            if s.isdigit():
                ids.append(int(s))
        return ids

settings = Settings()
