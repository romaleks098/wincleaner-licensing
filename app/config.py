import os
from typing import List, Optional
from pydantic_settings import BaseSettings, SettingsConfigDict

_SERVER_DIR = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
_ENV_FILE = os.path.join(_SERVER_DIR, ".env")

class Settings(BaseSettings):
    model_config = SettingsConfigDict(
        env_file=(_ENV_FILE, ".env"),
        env_file_encoding="utf-8",
        extra="allow"
    )

    HOST: str = "0.0.0.0"
    PORT: int = 8000
    ENVIRONMENT: str = "production"
    DATABASE_URL: str = "sqlite+aiosqlite:///./licensing.db"
    BOT_TOKEN: str = ""
    BOT_USERNAME: str = "WinCleanerLicense_Bot"
    ADMIN_IDS: str = ""
    ADMIN_USERNAME: Optional[str] = None
    ED25519_PRIVATE_KEY_HEX: str = "adbb08e9c7ed6ee0cee470ea624cc9a55b160c84ea15858cd619e0a94f18139e"
    ED25519_PUBLIC_KEY_HEX: str = "acba32615e13044a3d15823b07ed600e9384c34b79eab06aa88f8c08fada9457"
    ADMIN_SECRET: str = "super-secret-admin-token-for-api-calls"

    # Asset paths
    ASSETS_DIR: str = os.path.join(_SERVER_DIR, "assets")
    BANNERS_DIR: str = os.path.join(_SERVER_DIR, "assets", "banners")

    @property
    def banner_main(self) -> str:
        return os.path.join(self.BANNERS_DIR, "banner_main.png")

    @property
    def banner_tariffs(self) -> str:
        return os.path.join(self.BANNERS_DIR, "banner_tariffs.png")

    @property
    def banner_support(self) -> str:
        return os.path.join(self.BANNERS_DIR, "banner_support.png")

    @property
    def banner_activate(self) -> str:
        return os.path.join(self.BANNERS_DIR, "banner_activate.png")

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
