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
    # 1. Create all missing tables defined in Base.metadata
    await conn.run_sync(Base.metadata.create_all)

    # 2. Add is_banned column to users table if missing
    try:
        dialect_name = conn.dialect.name
        if dialect_name == "postgresql":
            # PostgreSQL dialect-safe migration
            await conn.execute(text("""
                DO $$ 
                BEGIN 
                    IF NOT EXISTS (
                        SELECT 1 FROM information_schema.columns 
                        WHERE table_name = 'users' AND column_name = 'is_banned'
                    ) THEN 
                        ALTER TABLE users ADD COLUMN is_banned BOOLEAN DEFAULT FALSE; 
                    END IF; 
                END $$;
            """))
        else:
            # SQLite dialect-safe migration
            res = await conn.execute(text("PRAGMA table_info(users);"))
            rows = res.fetchall()
            col_names = [r[1] for r in rows] if rows else []
            if "is_banned" not in col_names:
                await conn.execute(text("ALTER TABLE users ADD COLUMN is_banned BOOLEAN DEFAULT 0;"))
    except Exception as e:
        logger.warning(f"Schema migration warning for is_banned column: {e}")

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
