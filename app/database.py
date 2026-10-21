import asyncio
import logging
from typing import AsyncGenerator
from sqlalchemy import text
from sqlalchemy.ext.asyncio import create_async_engine, AsyncSession, async_sessionmaker
from sqlalchemy.orm import declarative_base
from app.config import settings

logger = logging.getLogger("database")
Base = declarative_base()

engine = create_async_engine(
    settings.async_database_url,
    echo=False,
    pool_pre_ping=True
)

AsyncSessionLocal = async_sessionmaker(
    bind=engine,
    class_=AsyncSession,
    expire_on_commit=False,
    autocommit=False,
    autoflush=False
)

async def _run_schema_migrations(conn):
    """
    Ensures all tables and schema changes are created reliably
    without failing across different SQL dialects (SQLite & PostgreSQL).
    """
    # 1. Create all missing tables defined in Base.metadata (orders, promo_codes, referral_history, etc.)
    await conn.run_sync(Base.metadata.create_all)

    # 2. Add columns if missing
    dialect_name = conn.dialect.name
    try:
        if dialect_name == "postgresql":
            # PostgreSQL dialect-safe migration
            await conn.execute(text("""
                DO $$ 
                BEGIN 
                    -- users table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'is_banned') THEN 
                        ALTER TABLE users ADD COLUMN is_banned BOOLEAN DEFAULT FALSE; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'balance') THEN 
                        ALTER TABLE users ADD COLUMN balance INTEGER DEFAULT 0; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'referrer_id') THEN 
                        ALTER TABLE users ADD COLUMN referrer_id INTEGER DEFAULT NULL; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'users' AND column_name = 'referral_code') THEN 
                        ALTER TABLE users ADD COLUMN referral_code VARCHAR(32) DEFAULT NULL; 
                    END IF; 

                    -- licenses table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'licenses' AND column_name = 'reminder_3d_sent') THEN 
                        ALTER TABLE licenses ADD COLUMN reminder_3d_sent BOOLEAN DEFAULT FALSE; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'licenses' AND column_name = 'reminder_1d_sent') THEN 
                        ALTER TABLE licenses ADD COLUMN reminder_1d_sent BOOLEAN DEFAULT FALSE; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'licenses' AND column_name = 'expired_sent') THEN 
                        ALTER TABLE licenses ADD COLUMN expired_sent BOOLEAN DEFAULT FALSE; 
                    END IF; 

                    -- devices table
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'devices' AND column_name = 'os_version') THEN 
                        ALTER TABLE devices ADD COLUMN os_version VARCHAR(128) DEFAULT 'Windows 10/11'; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'devices' AND column_name = 'app_version') THEN 
                        ALTER TABLE devices ADD COLUMN app_version VARCHAR(32) DEFAULT '2.0.0'; 
                    END IF; 
                    IF NOT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'devices' AND column_name = 'ip_address') THEN 
                        ALTER TABLE devices ADD COLUMN ip_address VARCHAR(64) DEFAULT NULL; 
                    END IF; 
                END $$;
            """))
        else:
            # SQLite dialect-safe migration
            async def add_sqlite_col(table: str, col: str, col_type: str):
                res = await conn.execute(text(f"PRAGMA table_info({table});"))
                rows = res.fetchall()
                col_names = [r[1] for r in rows] if rows else []
                if col not in col_names:
                    await conn.execute(text(f"ALTER TABLE {table} ADD COLUMN {col} {col_type};"))

            # users
            await add_sqlite_col("users", "is_banned", "BOOLEAN DEFAULT 0")
            await add_sqlite_col("users", "balance", "INTEGER DEFAULT 0")
            await add_sqlite_col("users", "referrer_id", "INTEGER DEFAULT NULL")
            await add_sqlite_col("users", "referral_code", "VARCHAR(32) DEFAULT NULL")

            # licenses
            await add_sqlite_col("licenses", "reminder_3d_sent", "BOOLEAN DEFAULT 0")
            await add_sqlite_col("licenses", "reminder_1d_sent", "BOOLEAN DEFAULT 0")
            await add_sqlite_col("licenses", "expired_sent", "BOOLEAN DEFAULT 0")

            # devices
            await add_sqlite_col("devices", "os_version", "VARCHAR(128) DEFAULT 'Windows 10/11'")
            await add_sqlite_col("devices", "app_version", "VARCHAR(32) DEFAULT '2.0.0'")
            await add_sqlite_col("devices", "ip_address", "VARCHAR(64) DEFAULT NULL")

    except Exception as e:
        logger.warning(f"Schema migration warning: {e}")

async def init_db():
    global engine, AsyncSessionLocal
    for attempt in range(1, 6):
        try:
            logger.info(f"Connecting to database (attempt {attempt}/5)...")
            async with engine.begin() as conn:
                await _run_schema_migrations(conn)
            logger.info("Database connected and schema initialized.")
            return
        except Exception as e:
            logger.warning(f"Database connection attempt {attempt} failed: {e}")
            await asyncio.sleep(2)

    logger.warning("Primary database unavailable — switching to local SQLite fallback database...")
    try:
        engine = create_async_engine("sqlite+aiosqlite:///./licensing.db", echo=False)
        AsyncSessionLocal.configure(bind=engine)
        async with engine.begin() as conn:
            await _run_schema_migrations(conn)
        logger.info("SQLite fallback database initialized successfully.")
    except Exception as e:
        logger.error(f"Fallback SQLite error: {e}")

async def get_db() -> AsyncGenerator[AsyncSession, None]:
    async with AsyncSessionLocal() as session:
        try:
            yield session
        finally:
            await session.close()
